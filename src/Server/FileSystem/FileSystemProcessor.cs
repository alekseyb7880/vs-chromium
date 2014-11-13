﻿// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using VsChromium.Core.Collections;
using VsChromium.Core.Files;
using VsChromium.Core.Linq;
using VsChromium.Core.Logging;
using VsChromium.Server.FileSystemNames;
using VsChromium.Server.FileSystemSnapshot;
using VsChromium.Server.Operations;
using VsChromium.Server.Projects;
using VsChromium.Server.Threads;

namespace VsChromium.Server.FileSystem {
  [Export(typeof(IFileSystemProcessor))]
  public class FileSystemProcessor : IFileSystemProcessor {
    private static readonly TaskId FlushFileRegistrationQueueTaskId = new TaskId("FlushFileRegistrationQueueTaskId");
    private static readonly TaskId FlushPathsChangedQueueTaskId = new TaskId("FlushPathsChangedQueueTaskId");
    /// <summary>
    /// Performance optimization flag: when building a new file system tree
    /// snapshot, this flag enables code to try to re-use filename instances
    /// from the previous snapshot. This is currently turned off, as profiling
    /// showed this slows down the algorithm by about 40% with not advantage
    /// other than decreasing GC activity. Note that this actually didn't
    /// decrease memory usage, as FileName instances are orphaned when a new
    /// snapshot is created (and the previous one is released).
    /// </summary>
    private static readonly bool ReuseFileNameInstances = false;

    private readonly HashSet<FullPath> _registeredFiles = new HashSet<FullPath>();
    private readonly IProjectDiscovery _projectDiscovery;
    private readonly IDirectoryChangeWatcher _directoryChangeWatcher;
    private readonly IFileSystemNameFactory _fileSystemNameFactory;
    private readonly IFileSystem _fileSystem;
    private readonly object _lock = new object();
    private readonly IFileSystemSnapshotBuilder _fileSystemSnapshotBuilder;
    private readonly IOperationProcessor _operationProcessor;
    private readonly ITaskQueue _taskQueue;
    private readonly FileRegistrationQueue _fileRegistrationQueue = new FileRegistrationQueue();
    private readonly SimpleConcurrentQueue<IList<PathChangeEntry>> _pathsChangedQueue = new SimpleConcurrentQueue<IList<PathChangeEntry>>();
    private FileSystemTreeSnapshot _fileSystemSnapshot;
    private int _version;

    [ImportingConstructor]
    public FileSystemProcessor(
      IFileSystemNameFactory fileSystemNameFactory,
      IFileSystem fileSystem,
      IFileSystemSnapshotBuilder fileSystemSnapshotBuilder,
      IOperationProcessor operationProcessor,
      IProjectDiscovery projectDiscovery,
      IDirectoryChangeWatcherFactory directoryChangeWatcherFactory,
      ITaskQueueFactory taskQueueFactory) {
      _fileSystemNameFactory = fileSystemNameFactory;
      _fileSystem = fileSystem;
      _fileSystemSnapshotBuilder = fileSystemSnapshotBuilder;
      _operationProcessor = operationProcessor;
      _projectDiscovery = projectDiscovery;

      _taskQueue = taskQueueFactory.CreateQueue("FileSystemProcessor Task Queue");
      _fileSystemSnapshot = FileSystemTreeSnapshot.Empty;
      _directoryChangeWatcher = directoryChangeWatcherFactory.CreateWatcher();
      _directoryChangeWatcher.PathsChanged += DirectoryChangeWatcherOnPathsChanged;
    }

    public FileSystemTreeSnapshot GetCurrentSnapshot() {
      lock (_lock) {
        return _fileSystemSnapshot;
      }
    }

    public void RegisterFile(FullPath path) {
      _fileRegistrationQueue.Enqueue(FileRegistrationKind.Register, path);
      _taskQueue.Enqueue(FlushFileRegistrationQueueTaskId, FlushFileRegistrationQueueTask);
    }

    public void UnregisterFile(FullPath path) {
      _fileRegistrationQueue.Enqueue(FileRegistrationKind.Unregister, path);
      _taskQueue.Enqueue(FlushFileRegistrationQueueTaskId, FlushFileRegistrationQueueTask);
    }

    public event EventHandler<OperationInfo> SnapshotComputing;
    public event EventHandler<SnapshotComputedResult> SnapshotComputed;
    public event EventHandler<FilesChangedEventArgs> FilesChanged;

    protected virtual void OnSnapshotComputing(OperationInfo e) {
      EventHandler<OperationInfo> handler = SnapshotComputing;
      if (handler != null) handler(this, e);
    }

    protected virtual void OnSnapshotComputed(SnapshotComputedResult e) {
      EventHandler<SnapshotComputedResult> handler = SnapshotComputed;
      if (handler != null) handler(this, e);
    }

    protected virtual void OnFilesChanged(FilesChangedEventArgs e) {
      EventHandler<FilesChangedEventArgs> handler = FilesChanged;
      if (handler != null) handler(this, e);
    }

    private void DirectoryChangeWatcherOnPathsChanged(IList<PathChangeEntry> changes) {
      _pathsChangedQueue.Enqueue(changes);
      _taskQueue.Enqueue(FlushPathsChangedQueueTaskId, FlushPathsChangedQueueTask);
    }

    private void FlushPathsChangedQueueTask() {
      var changes = _pathsChangedQueue
        .DequeueAll()
        .SelectMany(x => x)
        .ToList();

      var validationResult =
        new FileSystemChangesValidator(_fileSystemNameFactory, _projectDiscovery).ProcessPathsChangedEvent(changes);
      if (validationResult.RecomputeGraph) {
        RecomputeGraph();
      } else if (validationResult.ChangedFiles.Any()) {
        OnFilesChanged(new FilesChangedEventArgs {
          ChangedFiles = validationResult.ChangedFiles.ToReadOnlyCollection()
        });
      }
    }

    private void FlushFileRegistrationQueueTask() {
      var entries = _fileRegistrationQueue.DequeueAll();
      if (!entries.Any())
        return;

      Logger.Log("FlushFileRegistrationQueueTask:");
      foreach (var entry in entries) {
        Logger.Log("    Path=\"{0}\", Kind={1}", entry.Path, entry.Kind);
      }

      bool recompute = ValidateKnownFiles();

      lock (_lock) {
        // Take a snapshot of all known project paths before applying changes
        var projectPaths1 = CollectKnownProjectPathsSorted(_registeredFiles);

        // Apply changes
        foreach (var entry in entries) {
          switch (entry.Kind) {
            case FileRegistrationKind.Register:
              _registeredFiles.Add(entry.Path);
              break;
            case FileRegistrationKind.Unregister:
              _registeredFiles.Remove(entry.Path);
              break;
            default:
              throw new ArgumentOutOfRangeException();
          }
        }

        // Take a snapshot after applying changes, and compare
        var projectPaths2 = CollectKnownProjectPathsSorted(_registeredFiles);
        if (!projectPaths1.SequenceEqual(projectPaths2)) {
          recompute = true;
        }
      }

      if (recompute)
        RecomputeGraph();
    }

    private IEnumerable<FullPath> CollectKnownProjectPathsSorted(IEnumerable<FullPath> knownFileNames) {
      return knownFileNames
        .Select(x => _projectDiscovery.GetProjectPath(x))
        .Where(x => x != default(FullPath))
        .Distinct()
        .OrderBy(x => x)
        .ToList();
    }

    /// <summary>
    /// Sanety check: remove all files that don't exist on the file system anymore.
    /// </summary>
    private bool ValidateKnownFiles() {
      // Reset our knowledge about the file system, as a safety measure, since we don't
      // currently fully implement watching all changes in the file system that could affect
      // the cache. For example, if a ".chromium-project" file is added to a child
      // directory of a file we have been notified, it could totally change how we compute
      // the world.
      _projectDiscovery.ValidateCache();

      // We take the lock twice because we want to avoid calling "File.Exists" inside
      // the lock.
      IList<FullPath> filenames;
      lock (_lock) {
        filenames = _registeredFiles.ToList();
      }

      var deletedFileNames = filenames.Where(x => !_fileSystem.FileExists(x)).ToList();

      if (deletedFileNames.Any()) {
        Logger.Log("Some known files do not exist on disk anymore. Time to recompute the world.");
        lock (_lock) {
          deletedFileNames.ForEach(x => _registeredFiles.Remove(x));
        }
        return true;
      }

      return false;
    }

    private void RecomputeGraph() {
      _operationProcessor.Execute(new OperationHandlers {
        OnBeforeExecute = info => OnSnapshotComputing(info),
        OnError = (info, error) => OnSnapshotComputed(new SnapshotComputedResult { OperationInfo = info, Error = error }),
        Execute = info => {
          Logger.Log("Collecting list of files from file system.");
          Logger.LogMemoryStats();
          var sw = Stopwatch.StartNew();

          var files = new List<FullPath>();
          lock (_lock) {
            ValidateKnownFiles();
            files.AddRange(_registeredFiles);
          }

          IFileSystemNameFactory fileNameFactory = _fileSystemNameFactory;
          if (ReuseFileNameInstances) {
            if (_fileSystemSnapshot.ProjectRoots.Count > 0) {
              fileNameFactory = new FileSystemTreeSnapshotNameFactory(_fileSystemSnapshot, fileNameFactory);
            }
          }
          var newSnapshot = _fileSystemSnapshotBuilder.Compute(fileNameFactory, files, Interlocked.Increment(ref _version));

          // Monitor all the Chromium directories for changes.
          var newRoots = newSnapshot.ProjectRoots
            .Select(entry => entry.Directory.DirectoryName.FullPath);
          _directoryChangeWatcher.WatchDirectories(newRoots);

          // Update current tree atomically
          FileSystemTreeSnapshot previousSnapshot;
          lock (_lock) {
            previousSnapshot = _fileSystemSnapshot;
            _fileSystemSnapshot = newSnapshot;
          }

          sw.Stop();
          Logger.Log(">>>>>>>> Done collecting list of files: {0:n0} files in {1:n0} directories collected in {2:n0} msec.",
            newSnapshot.ProjectRoots.Aggregate(0, (acc, x) => acc + CountFileEntries(x.Directory)),
            newSnapshot.ProjectRoots.Aggregate(0, (acc, x) => acc + CountDirectoryEntries(x.Directory)),
            sw.ElapsedMilliseconds);
          Logger.LogMemoryStats();

          OnSnapshotComputed(new SnapshotComputedResult {
            OperationInfo = info,
            PreviousSnapshot = previousSnapshot,
            NewSnapshot = newSnapshot
          });
        }
      });
    }

    private int CountFileEntries(DirectorySnapshot entry) {
      return
        entry.ChildFiles.Count +
        entry.ChildDirectories.Aggregate(0, (acc, x) => acc + CountFileEntries(x));
    }

    private int CountDirectoryEntries(DirectorySnapshot entry) {
      return
        1 +
        entry.ChildDirectories.Aggregate(0, (acc, x) => acc + CountDirectoryEntries(x));
    }
  }
}