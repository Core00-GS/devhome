﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Management.Automation;
using DevHome.Common.Services;
using LibGit2Sharp;
using Microsoft.Windows.DevHome.SDK;
using Serilog;

namespace FileExplorerGitIntegration.Models;

internal sealed class RepositoryWrapper : IDisposable
{
    private readonly Repository _repo;
    private readonly ReaderWriterLockSlim _repoLock = new();

    private readonly string _workingDirectory;

    private readonly StatusCache _statusCache;

    private readonly StringResource _stringResource = new("FileExplorerGitIntegration.pri", "Resources");
    private readonly string _folderStatusBranch;
    private readonly string _folderStatusDetached;
    private readonly string _fileStatusMergeConflict;
    private readonly string _fileStatusUntracked;
    private readonly string _fileStatusStaged;
    private readonly string _fileStatusStagedRenamed;
    private readonly string _fileStatusStagedModified;
    private readonly string _fileStatusStagedRenamedModified;
    private readonly string _fileStatusModified;
    private readonly string _fileStatusRenamedModified;

    private Commit? _head;
    private CommitLogCache? _commits;

    private bool _disposedValue;

    public RepositoryWrapper(string rootFolder)
    {
        _repo = new Repository(rootFolder);
        _workingDirectory = _repo.Info.WorkingDirectory;
        _statusCache = new StatusCache(rootFolder);

        _folderStatusBranch = _stringResource.GetLocalized("FolderStatusBranch");
        _folderStatusDetached = _stringResource.GetLocalized("FolderStatusDetached");
        _fileStatusMergeConflict = _stringResource.GetLocalized("FileStatusMergeConflict");
        _fileStatusUntracked = _stringResource.GetLocalized("FileStatusUntracked");
        _fileStatusStaged = _stringResource.GetLocalized("FileStatusStaged");
        _fileStatusStagedRenamed = _stringResource.GetLocalized("FileStatusStagedRenamed");
        _fileStatusStagedModified = _stringResource.GetLocalized("FileStatusStagedModified");
        _fileStatusStagedRenamedModified = _stringResource.GetLocalized("FileStatusStagedRenamedModified");
        _fileStatusModified = _stringResource.GetLocalized("FileStatusModified");
        _fileStatusRenamedModified = _stringResource.GetLocalized("FileStatusRenamedModified");
    }

    public CommitWrapper? FindLastCommit(string relativePath)
    {
        // Fetching the most recent status to check if the file is renamed
        // should be much less expensive than getting the most recent commit.
        // So, preemtively check for a rename here.
        var commitLog = GetCommitLogCache();
        return commitLog.FindLastCommit(GetOriginalPath(relativePath));
    }

    private CommitLogCache GetCommitLogCache()
    {
        // Fast path: if we have an up-to-date commit log, return that
        if (_head != null && _commits != null)
        {
            _repoLock.EnterReadLock();
            try
            {
                if (_repo.Head.Tip == _head)
                {
                    return _commits;
                }
            }
            finally
            {
                _repoLock?.ExitReadLock();
            }
        }

        // Either the commit log hasn't been created yet, or it's out of date
        _repoLock.EnterWriteLock();
        try
        {
            if (_head == null || _commits == null || _repo.Head.Tip != _head)
            {
                _commits = new CommitLogCache(_repo);
                _head = _repo.Head.Tip;
            }
        }
        finally
        {
            _repoLock.ExitWriteLock();
        }

        return _commits;
    }

    public string GetRepoStatus()
    {
        var repoStatus = _statusCache.Status;

        string branchName;
        var branchStatus = string.Empty;
        try
        {
            _repoLock.EnterWriteLock();
            branchName = _repo.Info.IsHeadDetached ?
                string.Format(CultureInfo.CurrentCulture, _folderStatusDetached, _repo.Head.Tip.Sha[..7]) :
                string.Format(CultureInfo.CurrentCulture, _folderStatusBranch, _repo.Head.FriendlyName);
            if (_repo.Head.IsTracking)
            {
                var behind = _repo.Head.TrackingDetails.BehindBy;
                var ahead = _repo.Head.TrackingDetails.AheadBy;
                if (behind == 0 && ahead == 0)
                {
                    branchStatus = " ≡";
                }
                else if (behind > 0 && ahead > 0)
                {
                    branchStatus = " ↓" + behind + " ↑" + ahead;
                }
                else if (behind > 0)
                {
                    branchStatus = " ↓" + behind;
                }
                else if (ahead > 0)
                {
                    branchStatus = " ↑" + ahead;
                }
            }
        }
        finally
        {
            _repoLock.ExitWriteLock();
        }

        var fileStatus = $"| +{repoStatus.Added.Count} ~{repoStatus.Staged.Count + repoStatus.RenamedInIndex.Count} -{repoStatus.Removed.Count} | +{repoStatus.Untracked.Count} ~{repoStatus.Modified.Count + repoStatus.RenamedInWorkDir.Count} -{repoStatus.Missing.Count}";
        var conflicted = repoStatus.Conflicted.Count;

        if (conflicted > 0)
        {
            fileStatus += $" !{conflicted}";
        }

        return branchName + branchStatus + " " + fileStatus;
    }

    public string GetFileStatus(string relativePath)
    {
        GitStatusEntry? status;
        if (!_statusCache.Status.FileEntries.TryGetValue(relativePath, out status))
        {
            return string.Empty;
        }

        if (status.Status == FileStatus.Unaltered || status.Status.HasFlag(FileStatus.Nonexistent | FileStatus.Ignored))
        {
            return string.Empty;
        }
        else if (status.Status.HasFlag(FileStatus.Conflicted))
        {
            return _fileStatusMergeConflict;
        }
        else if (status.Status.HasFlag(FileStatus.NewInWorkdir))
        {
            return _fileStatusUntracked;
        }

        var statusString = string.Empty;
        var staged = status.Status.HasFlag(FileStatus.NewInIndex) || status.Status.HasFlag(FileStatus.ModifiedInIndex) || status.Status.HasFlag(FileStatus.RenamedInIndex) || status.Status.HasFlag(FileStatus.TypeChangeInIndex);
        var modified = status.Status.HasFlag(FileStatus.ModifiedInWorkdir) || status.Status.HasFlag(FileStatus.TypeChangeInWorkdir);
        var renamed = status.Status.HasFlag(FileStatus.RenamedInIndex) || status.Status.HasFlag(FileStatus.RenamedInWorkdir);

        if (staged)
        {
            if (renamed && modified)
            {
                statusString = _fileStatusStagedRenamedModified;
            }
            else if (renamed)
            {
                statusString = _fileStatusStagedRenamed;
            }
            else if (modified)
            {
                statusString = _fileStatusStagedModified;
            }
            else
            {
                statusString = _fileStatusStaged;
            }
        }
        else if (modified)
        {
            if (renamed)
            {
                statusString = _fileStatusRenamedModified;
            }
            else
            {
                statusString = _fileStatusModified;
            }
        }

        return statusString;
    }

    // Detect uncommitted renames and return the original path.
    // This allows us to get the commit history, because the new path doesn't exist yet.
    private string GetOriginalPath(string relativePath)
    {
        _statusCache.Status.FileEntries.TryGetValue(relativePath, out var status);
        if (status is null)
        {
            return relativePath;
        }

        if (status.Status.HasFlag(FileStatus.RenamedInIndex) || status.Status.HasFlag(FileStatus.RenamedInWorkdir))
        {
            return status.RenameOldPath ?? relativePath;
        }

        return relativePath;
    }

    internal void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _repo.Dispose();
                _repoLock.Dispose();
            }
        }

        _disposedValue = true;
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
