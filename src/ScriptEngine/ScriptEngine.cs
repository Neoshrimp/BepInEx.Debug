﻿using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Common;
using HarmonyLib;
using UnityEngine;

namespace ScriptEngine
{
    [BepInPlugin(GUID, "Script Engine", Version)]
    public class ScriptEngine : BaseUnityPlugin
    {
        public const string GUID = "com.bepis.bepinex.scriptengine";
        public const string Version = Metadata.Version;

        public string ScriptDirectory => Path.Combine(Paths.BepInExRootPath, "scripts");

        private GameObject scriptManager;

        private ConfigEntry<bool> LoadOnStart { get; set; }
        private ConfigEntry<KeyboardShortcut> ReloadKey { get; set; }
        private ConfigEntry<bool> QuietMode { get; set; }
        private ConfigEntry<bool> EnableFileSystemWatcher { get; set; }
        private ConfigEntry<bool> IncludeSubdirectories { get; set; }
        private ConfigEntry<float> AutoReloadDelay { get; set; }

        private FileSystemWatcher fileSystemWatcher;
        private bool shouldReload;
        private float autoReloadTimer;

        private void Awake()
        {
            LoadOnStart = Config.Bind("General", "LoadOnStart", true, new ConfigDescription("Load all plugins from the scripts folder when starting the application. This is done from inside of Chainloader's Awake, therefore not all plugis might be loaded yet. BepInDependency attributes are ignored."));
            ReloadKey = Config.Bind("General", "ReloadKey", new KeyboardShortcut(KeyCode.F7), new ConfigDescription("Press this key to reload all the plugins from the scripts folder"));
            QuietMode = Config.Bind("General", "QuietMode", false, new ConfigDescription("Disable all logging except for error messages."));
            IncludeSubdirectories = Config.Bind("General", "IncludeSubdirectories", true, new ConfigDescription("Also load plugins from subdirectories of the scripts folder."));
            EnableFileSystemWatcher = Config.Bind("AutoReload", "EnableFileSystemWatcher", false, new ConfigDescription("Watches the scripts directory for file changes and automatically reloads all plugins if any of the files gets changed (added/removed/modified)."));
            AutoReloadDelay = Config.Bind("AutoReload", "AutoReloadDelay", 3.0f, new ConfigDescription("Delay in seconds from detecting a change to files in the scripts directory to plugins being reloaded. Affects only EnableFileSystemWatcher."));


            DontDestroyOnLoad(this.gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            if (LoadOnStart.Value)
                ReloadPlugins();

            if (EnableFileSystemWatcher.Value)
                StartFileSystemWatcher();
        }

        private void Update()
        {
            if (ReloadKey.Value.IsDown())
            {
                ReloadPlugins();
            }
            else if (shouldReload)
            {
                autoReloadTimer -= Time.unscaledDeltaTime;
                if (autoReloadTimer <= .0f)
                    ReloadPlugins();
            }
        }

        public void ReloadPlugins()
        {
            shouldReload = false;

            if (scriptManager != null)
            {
                if (!QuietMode.Value) Logger.Log(LogLevel.Info, "Unloading old plugin instances");

                foreach (var previouslyLoadedPlugin in scriptManager.GetComponents<BaseUnityPlugin>())
                {
                    var metadataGUID = previouslyLoadedPlugin.Info.Metadata.GUID;
                    if (Chainloader.PluginInfos.ContainsKey(metadataGUID))
                        Chainloader.PluginInfos.Remove(metadataGUID);
                }

                Destroy(scriptManager);
            }

            scriptManager = new GameObject($"ScriptEngine_{DateTime.Now.Ticks}");
            DontDestroyOnLoad(scriptManager);
            scriptManager.hideFlags = HideFlags.HideAndDontSave;

            var files = Directory.GetFiles(ScriptDirectory, "*.dll", IncludeSubdirectories.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            if (files.Length > 0)
            {
                foreach (string path in Directory.GetFiles(ScriptDirectory, "*.dll", IncludeSubdirectories.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                    LoadDLL(path, scriptManager);

                if (!QuietMode.Value)
                    Logger.LogMessage("Reloaded all plugins!");
            }
            else
            {
                if (!QuietMode.Value)
                    Logger.LogMessage("No plugins to reload");
            }
        }

        private void LoadDLL(string path, GameObject obj)
        {
            var defaultResolver = new DefaultAssemblyResolver();
            defaultResolver.AddSearchDirectory(ScriptDirectory);
            defaultResolver.AddSearchDirectory(Paths.ManagedPath);
            defaultResolver.AddSearchDirectory(Paths.BepInExAssemblyDirectory);

            if (!QuietMode.Value)
                Logger.Log(LogLevel.Info, $"Loading plugins from {path}");

            using (var dll = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = defaultResolver }))
            {
                dll.Name.Name = $"{dll.Name.Name}-{DateTime.Now.Ticks}";

                using (var ms = new MemoryStream())
                {
                    dll.Write(ms);
                    var ass = Assembly.Load(ms.ToArray());

                    foreach (Type type in GetTypesSafe(ass))
                    {
                        try
                        {
                            if (!typeof(BaseUnityPlugin).IsAssignableFrom(type)) continue;

                            var metadata = MetadataHelper.GetMetadata(type);
                            if (metadata == null) continue;

                            if (!QuietMode.Value)
                                Logger.Log(LogLevel.Info, $"Loading {metadata.GUID}");

                            if (Chainloader.PluginInfos.TryGetValue(metadata.GUID, out var existingPluginInfo))
                                throw new InvalidOperationException($"A plugin with GUID {metadata.GUID} is already loaded! ({existingPluginInfo.Metadata.Name} v{existingPluginInfo.Metadata.Version})");

                            var typeDefinition = dll.MainModule.Types.First(x => x.FullName == type.FullName);
                            var pluginInfo = Chainloader.ToPluginInfo(typeDefinition);

                            StartCoroutine(DelayAction(() =>
                            {
                                try
                                {
                                    // Need to add to PluginInfos first because BaseUnityPlugin constructor (called by AddComponent below)
                                    // looks in PluginInfos for an existing PluginInfo and uses it instead of creating a new one.
                                    Chainloader.PluginInfos[metadata.GUID] = pluginInfo;

                                    object instance = null;

                                    // Fill in properties that are normally set by Chainloader
                                    var tv = Traverse.Create(pluginInfo);
                                    tv.Property<BaseUnityPlugin>(nameof(pluginInfo.Instance)).Value = (BaseUnityPlugin)instance;
                                    // Loading the assembly from memory causes Location to be lost
                                    tv.Property<string>(nameof(pluginInfo.Location)).Value = path;

                                    // instantiate Plugin object after info has been set
                                    instance = obj.AddComponent(type);
                                }
                                catch (Exception e)
                                {
                                    Logger.LogError($"Failed to load plugin {metadata.GUID} because of exception: {e}");
                                    Chainloader.PluginInfos.Remove(metadata.GUID);
                                }
                            }));
                        }
                        catch (Exception e)
                        {
                            Logger.LogError($"Failed to load plugin {type.Name} because of exception: {e}");
                        }
                    }
                }
            }
        }

        private void StartFileSystemWatcher()
        {
            fileSystemWatcher = new FileSystemWatcher(ScriptDirectory)
            {
                IncludeSubdirectories = IncludeSubdirectories.Value
            };
            fileSystemWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            fileSystemWatcher.Filter = "*.dll";
            fileSystemWatcher.Changed += FileChangedEventHandler;
            fileSystemWatcher.Deleted += FileChangedEventHandler;
            fileSystemWatcher.Created += FileChangedEventHandler;
            fileSystemWatcher.Renamed += FileChangedEventHandler;
            fileSystemWatcher.EnableRaisingEvents = true;
        }

        private void FileChangedEventHandler(object sender, FileSystemEventArgs args)
        {
            if (!QuietMode.Value)
                Logger.LogInfo($"File {Path.GetFileName(args.Name)} changed. Delayed recompiling...");
            shouldReload = true;
            autoReloadTimer = AutoReloadDelay.Value;
        }

        private IEnumerable<Type> GetTypesSafe(Assembly ass)
        {
            try
            {
                return ass.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var sbMessage = new StringBuilder();
                sbMessage.AppendLine("\r\n-- LoaderExceptions --");
                foreach (var l in ex.LoaderExceptions)
                    sbMessage.AppendLine(l.ToString());
                sbMessage.AppendLine("\r\n-- StackTrace --");
                sbMessage.AppendLine(ex.StackTrace);
                Logger.LogError(sbMessage.ToString());
                return ex.Types.Where(x => x != null);
            }
        }

        private IEnumerator DelayAction(Action action)
        {
            yield return null;
            action();
        }
    }
}
