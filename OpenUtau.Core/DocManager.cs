﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Api;
using OpenUtau.Classic;
using OpenUtau.Core.Lib;
using OpenUtau.Core.Ustx;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Core {
    public struct ValidateOptions {
        public bool SkipTiming;
        public UPart Part;
        public bool SkipPhonemizer;
        public bool SkipPhoneme;
    }

    public class DocManager : SingletonBase<DocManager> {
        DocManager() {
            Project = new UProject();
        }

        private Thread mainThread;
        private TaskScheduler mainScheduler;

        public int playPosTick = 0;

        public TaskScheduler MainScheduler => mainScheduler;
        public Plugin[] Plugins { get; private set; }
        public PhonemizerFactory[] PhonemizerFactories { get; private set; }
        public UProject Project { get; private set; }
        public bool HasOpenUndoGroup => undoGroup != null;
        public List<UPart> PartsClipboard { get; set; }
        public List<UNote> NotesClipboard { get; set; }
        internal PhonemizerRunner PhonemizerRunner { get; private set; }

        public void Initialize() {
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler((sender, args) => {
                CrashSave();
            });
            SearchAllPlugins();
            SearchAllLegacyPlugins();
            mainThread = Thread.CurrentThread;
            mainScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            PhonemizerRunner = new PhonemizerRunner(mainScheduler);
        }

        public void SearchAllLegacyPlugins() {
            try {
                var stopWatch = Stopwatch.StartNew();
                Plugins = PluginLoader.LoadAll(PathManager.Inst.PluginsPath);
                stopWatch.Stop();
                Log.Information($"Search all legacy plugins: {stopWatch.Elapsed}");
            } catch (Exception e) {
                Log.Error(e, "Failed to search legacy plugins.");
                Plugins = new Plugin[0];
            }
        }

        public void SearchAllPlugins() {
            const string kBuiltin = "OpenUtau.Plugin.Builtin.dll";
            var stopWatch = Stopwatch.StartNew();
            var phonemizerFactories = new List<PhonemizerFactory>();
            var files = new List<string>();
            try {
                files.Add(Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location), kBuiltin));
                Directory.CreateDirectory(PathManager.Inst.PluginsPath);
                string oldBuiltin = Path.Combine(PathManager.Inst.PluginsPath, kBuiltin);
                if (File.Exists(oldBuiltin)) {
                    File.Delete(oldBuiltin);
                }
                files.AddRange(Directory.EnumerateFiles(PathManager.Inst.PluginsPath, "*.dll", SearchOption.AllDirectories));
            } catch (Exception e) {
                Log.Error(e, "Failed to search plugins.");
            }
            foreach (var file in files) {
                Assembly assembly;
                try {
                    if (!LibraryLoader.IsManagedAssembly(file)) {
                        Log.Information($"Skipping {file}");
                        continue;
                    }
                    assembly = Assembly.LoadFile(file);
                    foreach (var type in assembly.GetExportedTypes()) {
                        if (!type.IsAbstract && type.IsSubclassOf(typeof(Phonemizer))) {
                            phonemizerFactories.Add(PhonemizerFactory.Get(type));
                        }
                    }
                } catch (Exception e) {
                    Log.Warning(e, $"Failed to load {file}.");
                    continue;
                }
            }
            foreach (var type in GetType().Assembly.GetExportedTypes()) {
                if (!type.IsAbstract && type.IsSubclassOf(typeof(Phonemizer))) {
                    phonemizerFactories.Add(PhonemizerFactory.Get(type));
                }
            }
            PhonemizerFactories = phonemizerFactories.OrderBy(factory => factory.tag).ToArray();
            stopWatch.Stop();
            Log.Information($"Search all plugins: {stopWatch.Elapsed}");
        }

        #region Command Queue

        readonly Deque<UCommandGroup> undoQueue = new Deque<UCommandGroup>();
        readonly Deque<UCommandGroup> redoQueue = new Deque<UCommandGroup>();
        UCommandGroup undoGroup = null;
        UCommandGroup savedPoint = null;
        UCommandGroup autosavedPoint = null;

        public bool ChangesSaved {
            get {
                return (Project.Saved || Project.tracks.Count == 0) &&
                    (undoQueue.Count > 0 && savedPoint == undoQueue.Last() || undoQueue.Count == 0 && savedPoint == null);
            }
        }

        private void CrashSave() {
            if (Project == null || string.IsNullOrEmpty(Project.FilePath)) {
                return;
            }
            try {
                string dir = Path.GetDirectoryName(Project.FilePath);
                string filename = Path.GetFileNameWithoutExtension(Project.FilePath);
                string backup = Path.Join(dir, filename + "-backup.ustx");
                Log.Information($"Saving backup {backup}.");
                Format.Ustx.Save(backup, Project);
                Log.Information($"Saved backup {backup}.");
            } catch (Exception e) {
                Log.Error(e, "Save backup failed.");
            }
        }

        public void AutoSave() {
            if (Project == null || string.IsNullOrEmpty(Project.FilePath) || !Project.Saved) {
                return;
            }
            if (undoQueue.LastOrDefault() == autosavedPoint) {
                Log.Information("Autosave skipped.");
                return;
            }
            try {
                string dir = Path.GetDirectoryName(Project.FilePath);
                string filename = Path.GetFileNameWithoutExtension(Project.FilePath);
                string backup = Path.Join(dir, filename + "-autosave.ustx");
                Log.Information($"Autosave {backup}.");
                Format.Ustx.AutoSave(backup, Project);
                Log.Information($"Autosaved {backup}.");
                autosavedPoint = undoQueue.LastOrDefault();
            } catch (Exception e) {
                Log.Error(e, "Autosave failed.");
            }
        }

        public void ExecuteCmd(UCommand cmd) {
            if (mainThread != Thread.CurrentThread && !(cmd is ProgressBarNotification)) {
                Log.Error($"{cmd} not on main thread");
            }
            if (cmd is UNotification) {
                if (cmd is SaveProjectNotification) {
                    var _cmd = cmd as SaveProjectNotification;
                    if (undoQueue.Count > 0) {
                        savedPoint = undoQueue.Last();
                    }
                    if (string.IsNullOrEmpty(_cmd.Path)) {
                        Format.Ustx.Save(Project.FilePath, Project);
                    } else {
                        Format.Ustx.Save(_cmd.Path, Project);
                    }
                } else if (cmd is LoadProjectNotification notification) {
                    undoQueue.Clear();
                    redoQueue.Clear();
                    undoGroup = null;
                    savedPoint = null;
                    autosavedPoint = null;
                    Project = notification.project;
                    playPosTick = 0;
                } else if (cmd is SetPlayPosTickNotification) {
                    var _cmd = cmd as SetPlayPosTickNotification;
                    playPosTick = _cmd.playPosTick;
                } else if (cmd is SingersChangedNotification) {
                    SingerManager.Inst.SearchAllSingers();
                } else if (cmd is ValidateProjectNotification) {
                    Project.ValidateFull();
                } else if (cmd is SingersRefreshedNotification || cmd is OtoChangedNotification) {
                    foreach (var track in Project.tracks) {
                        track.OnSingerRefreshed();
                    }
                    Project.ValidateFull();
                    if (cmd is OtoChangedNotification) {
                        ExecuteCmd(new PreRenderNotification());
                    }
                }
                Publish(cmd);
                if (!cmd.Silent) {
                    Log.Information($"Publish notification {cmd}");
                }
                return;
            }
            if (undoGroup == null) {
                Log.Error($"No active UndoGroup {cmd}");
                return;
            }
            undoGroup.Commands.Add(cmd);
            lock (Project) {
                cmd.Execute();
            }
            if (!cmd.Silent) {
                Log.Information($"ExecuteCmd {cmd}");
            }
            Publish(cmd);
            if (!undoGroup.DeferValidate) {
                Project.Validate(cmd.ValidateOptions);
            }
        }

        public void StartUndoGroup(bool deferValidate = false) {
            if (undoGroup != null) {
                Log.Error("undoGroup already started");
                EndUndoGroup();
            }
            undoGroup = new UCommandGroup(deferValidate);
            Log.Information("undoGroup started");
        }

        public void EndUndoGroup() {
            if (undoGroup == null) {
                Log.Error("No active undoGroup to end.");
                return;
            }
            if (undoGroup.Commands.Count > 0) {
                undoQueue.AddToBack(undoGroup);
                redoQueue.Clear();
            }
            while (undoQueue.Count > Util.Preferences.Default.UndoLimit) {
                undoQueue.RemoveFromFront();
            }
            if (undoGroup.DeferValidate) {
                Project.ValidateFull();
            }
            undoGroup.Merge();
            undoGroup = null;
            Log.Information("undoGroup ended");
            ExecuteCmd(new PreRenderNotification());
        }

        public void RollBackUndoGroup() {
            if (undoGroup == null) {
                Log.Error("No active undoGroup to rollback.");
                return;
            }
            for (int i = undoGroup.Commands.Count - 1; i >= 0; i--) {
                var cmd = undoGroup.Commands[i];
                cmd.Unexecute();
                if (i == 0) {
                    Project.ValidateFull();
                }
                Publish(cmd, true);
            }
            undoGroup.Commands.Clear();
        }

        public void Undo() {
            if (undoQueue.Count == 0) {
                return;
            }
            var group = undoQueue.RemoveFromBack();
            for (int i = group.Commands.Count - 1; i >= 0; i--) {
                var cmd = group.Commands[i];
                cmd.Unexecute();
                if (i == 0) {
                    Project.ValidateFull();
                }
                Publish(cmd, true);
            }
            redoQueue.AddToBack(group);
            ExecuteCmd(new PreRenderNotification());
        }

        public void Redo() {
            if (redoQueue.Count == 0) {
                return;
            }
            var group = redoQueue.RemoveFromBack();
            for (var i = 0; i < group.Commands.Count; i++) {
                var cmd = group.Commands[i];
                cmd.Execute();
                if (i == group.Commands.Count - 1) {
                    Project.ValidateFull();
                }
                Publish(cmd);
            }
            undoQueue.AddToBack(group);
            ExecuteCmd(new PreRenderNotification());
        }

        # endregion

        # region Command Subscribers

        private readonly object lockObj = new object();
        private readonly List<ICmdSubscriber> subscribers = new List<ICmdSubscriber>();

        public void AddSubscriber(ICmdSubscriber sub) {
            lock (lockObj) {
                if (!subscribers.Contains(sub)) {
                    subscribers.Add(sub);
                }
            }
        }

        public void RemoveSubscriber(ICmdSubscriber sub) {
            lock (lockObj) {
                if (subscribers.Contains(sub)) {
                    subscribers.Remove(sub);
                }
            }
        }

        private void Publish(UCommand cmd, bool isUndo = false) {
            lock (lockObj) {
                foreach (var sub in subscribers) {
                    sub.OnNext(cmd, isUndo);
                }
            }
        }

        #endregion
    }
}
