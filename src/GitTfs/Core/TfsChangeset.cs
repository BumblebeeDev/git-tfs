﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GitTfs.Core.TfsInterop;
using GitTfs.Util;

namespace GitTfs.Core
{
    public class TfsChangeset : ITfsChangeset
    {
        private readonly ITfsHelper _tfs;
        private readonly IChangeset _changeset;
        private readonly AuthorsFile _authors;
        private readonly string _cutPath;
        private readonly bool _cutPathForce;
        public TfsChangesetInfo Summary { get; set; }
        public int BaseChangesetId { get; set; }

        public TfsChangeset(ITfsHelper tfs, IChangeset changeset, AuthorsFile authors, string cutPath, bool cutPathForce)
        {
            _tfs = tfs;
            _changeset = changeset;
            _authors = authors;
            _cutPath = cutPath;
            _cutPathForce = cutPathForce;
            BaseChangesetId = _changeset.Changes.Max(c => c.Item.ChangesetId) - 1;
        }

        public LogEntry Apply(string lastCommit, IGitTreeModifier treeBuilder, ITfsWorkspace workspace, IDictionary<string, GitObject> initialTree, Action<Exception> ignorableErrorHandler)
        {
            if (initialTree.Empty())
                Summary.Remote.Repository.GetObjects(lastCommit, initialTree);
            var remoteRelativeLocalPath = GetPathRelativeToWorkspaceLocalPath(workspace);
            var resolver = new PathResolver(Summary.Remote, _cutPath, _cutPathForce, remoteRelativeLocalPath, initialTree);
            var sieve = new ChangeSieve(_changeset, resolver);
            if (sieve.RenameBranchCommmit)
            {
                IsRenameChangeset = true;
            }
            if (sieve.DeletesProject)
            {
                IsDeleteProjectChangeset = true;
            }
            _changeset.Get(workspace, sieve.GetChangesToFetch(), ignorableErrorHandler);
            var forceGetChanges = lastCommit == null;
            foreach (var change in sieve.GetChangesToApply(forceGetChanges))
            {
                ignorableErrorHandler.Catch(() =>
                {
                    Apply(change, treeBuilder, workspace, initialTree);
                });
            }
            return MakeNewLogEntry();
        }

        private void Apply(ApplicableChange change, IGitTreeModifier treeBuilder, ITfsWorkspace workspace, IDictionary<string, GitObject> initialTree)
        {
            switch (change.Type)
            {
                case ChangeType.Update:
                    Update(change, treeBuilder, workspace, initialTree);
                    break;
                case ChangeType.Delete:
                    Delete(change.GitPath, treeBuilder, initialTree);
                    break;
                default:
                    throw new NotImplementedException("Unsupported change type: " + change.Type);
            }
        }

        private void Update(ApplicableChange change, IGitTreeModifier treeBuilder, ITfsWorkspace workspace, IDictionary<string, GitObject> initialTree)
        {
            // Assume we cut path at the start, so add it back
            var tfsPath = change.GitPath;
            if (!string.IsNullOrEmpty(_cutPath))
                tfsPath = _cutPath + "/" + tfsPath;

            var localPath = workspace.GetLocalPath(tfsPath);
            if (File.Exists(localPath))
            {
                treeBuilder.Add(change.GitPath, localPath, change.Mode);
            }
            else
            {
                // Fallback to supplied path
                tfsPath = change.GitPath;
                localPath = workspace.GetLocalPath(tfsPath);
                if (File.Exists(localPath))
                {
                    treeBuilder.Add(change.GitPath, localPath, change.Mode);
                }
                else
                {
                    Trace.TraceInformation("Cannot checkout file '{0}' from TFS. Skip it", tfsPath);
                }
            }
        }

        public IEnumerable<TfsTreeEntry> GetTree()
        {
            return GetFullTree().Where(item => item.Item.ItemType == TfsItemType.File && !Summary.Remote.ShouldSkip(item.FullName));
        }

        public bool IsMergeChangeset
        {
            get
            {
                if (_changeset == null || _changeset.Changes == null || !_changeset.Changes.Any())
                    return false;
                return _changeset.Changes.Any(c => c.ChangeType.IncludesOneOf(TfsChangeType.Merge));
            }
        }

        public bool IsBranchChangeset
        {
            get
            {
                if (_changeset == null || _changeset.Changes == null || !_changeset.Changes.Any())
                    return false;
                return _changeset.Changes.Any(c => c.ChangeType == TfsChangeType.Branch);
            }
        }

        public IEnumerable<TfsTreeEntry> GetFullTree()
        {
            var treeInfo = Summary.Remote.Repository.CreateObjectsDictionary();
            var resolver = new PathResolver(Summary.Remote, _cutPath, _cutPathForce, "", treeInfo);

            IItem[] tfsItems;
            if (Summary.Remote.TfsRepositoryPath != null)
            {
                tfsItems = _changeset.VersionControlServer.GetItems(Summary.Remote.TfsRepositoryPath, _changeset.ChangesetId, TfsRecursionType.Full);
            }
            else
            {
                tfsItems = Summary.Remote.TfsSubtreePaths.SelectMany(x => _changeset.VersionControlServer.GetItems(x, _changeset.ChangesetId, TfsRecursionType.Full)).ToArray();
            }
            var tfsItemsWithGitPaths = tfsItems.Select(item => new { item, gitPath = resolver.GetPathInGitRepo(item.ServerItem) });
            return tfsItemsWithGitPaths.Where(x => x.gitPath != null).Select(x => new TfsTreeEntry(x.gitPath, x.item));
        }

        public LogEntry CopyTree(IGitTreeModifier treeBuilder, ITfsWorkspace workspace)
        {
            var startTime = DateTime.Now;
            var itemsCopied = 0;
            var maxChangesetId = 0;
            var tfsTreeEntries = GetTree().ToArray();
            if (tfsTreeEntries.Length == 0)
            {
                maxChangesetId = _changeset.ChangesetId;
            }
            else
            {
                workspace.Get(_changeset.ChangesetId);
                foreach (var entry in tfsTreeEntries)
                {
                    var tfsPath = entry.FullName;
                    if (!string.IsNullOrEmpty(Summary.Remote.TfsRepositoryPath) && entry.Item.ServerItem.StartsWith(Summary.Remote.TfsRepositoryPath))
                        tfsPath = entry.Item.ServerItem.Substring(Summary.Remote.TfsRepositoryPath.Length);
                    while (tfsPath.StartsWith("/"))
                        tfsPath = tfsPath.Substring(1);
                    Add(entry.Item, entry.FullName, tfsPath, treeBuilder, workspace);
                    maxChangesetId = Math.Max(maxChangesetId, entry.Item.ChangesetId);

                    itemsCopied++;
                    if (DateTime.Now - startTime > TimeSpan.FromSeconds(30))
                    {
                        Trace.TraceInformation("{0} objects created...", itemsCopied);
                        startTime = DateTime.Now;
                    }
                }
            }
            return MakeNewLogEntry(maxChangesetId == _changeset.ChangesetId ? _changeset : _tfs.GetChangeset(maxChangesetId));
        }

        private void Add(IItem item, string pathInGitRepo, string pathInTfsWorkspace, IGitTreeModifier treeBuilder, ITfsWorkspace workspace)
        {
            if (item.DeletionId == 0)
            {
                treeBuilder.Add(pathInGitRepo, workspace.GetLocalPath(pathInTfsWorkspace), LibGit2Sharp.Mode.NonExecutableFile);
            }
        }

        private void Delete(string pathInGitRepo, IGitTreeModifier treeBuilder, IDictionary<string, GitObject> initialTree)
        {
            if (initialTree.ContainsKey(pathInGitRepo))
            {
                treeBuilder.Remove(initialTree[pathInGitRepo].Path);
                Trace.WriteLine("\tD\t" + pathInGitRepo);
            }
        }

        private string GetPathRelativeToWorkspaceLocalPath(ITfsWorkspace workspace)
        {
            if (workspace.Remote.MatchesUrlAndRepositoryPath(Summary.Remote.TfsUrl, Summary.Remote.TfsRepositoryPath))
                return "";

            return string.IsNullOrEmpty(Summary.Remote.TfsRepositoryPath) ? "" : Summary.Remote.Prefix;
        }

        private LogEntry MakeNewLogEntry()
        {
            return MakeNewLogEntry(_changeset, Summary.Remote);
        }

        private LogEntry MakeNewLogEntry(IChangeset changesetToLog, IGitTfsRemote remote = null)
        {
            IIdentity identity = null;
            try
            {
                identity = _tfs.GetIdentity(changesetToLog.Committer);
            }
            catch
            {
            }
            var name = changesetToLog.Committer;
            var email = changesetToLog.Committer;
            if (_authors != null && _authors.Authors.ContainsKey(changesetToLog.Committer))
            {
                name = _authors.Authors[changesetToLog.Committer].Name;
                email = _authors.Authors[changesetToLog.Committer].Email;
            }
            else if (identity != null)
            {
                //This can be null if the user was deleted from AD.
                //We want to keep their original history around with as little
                //hassle to the end user as possible
                if (!string.IsNullOrWhiteSpace(identity.DisplayName))
                    name = identity.DisplayName;

                if (!string.IsNullOrWhiteSpace(identity.MailAddress))
                    email = identity.MailAddress;
            }
            else if (!string.IsNullOrWhiteSpace(changesetToLog.Committer))
            {
                string[] split = changesetToLog.Committer.Split('\\');
                if (split.Length == 2)
                {
                    name = split[1].ToLower();
                    email = string.Format("{0}@{1}.tfs.local", name, split[0].ToLower());
                }
            }

            // committer's & author's name and email MUST NOT be empty as otherwise they would be picked
            // by git from user.name and user.email config settings which is bad thing because commit could
            // be different depending on whose machine it fetched
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Unknown TFS user";
            }
            if (string.IsNullOrWhiteSpace(email))
            {
                email = "unknown@tfs.local";
            }
            if (remote == null)
                remote = Summary.Remote;
            return new LogEntry
            {
                Date = changesetToLog.CreationDate,
                Log = changesetToLog.Comment + Environment.NewLine,
                ChangesetId = changesetToLog.ChangesetId,
                CommitterName = name,
                AuthorName = name,
                CommitterEmail = email,
                AuthorEmail = email,
                Remote = remote
            };
        }

        public string OmittedParentBranch { get; set; }
        public bool IsRenameChangeset { get; set; }
        public bool IsDeleteProjectChangeset { get; set; }
    }
}
