using System;
using System.ComponentModel;
using System.Linq;
using NDesk.Options;
using GitTfs.Core;
using StructureMap;

namespace GitTfs.Commands
{
    [Pluggable("pull")]
    [Description("pull [options]")]
    [RequiresValidGitRepository]
    public class Pull : GitTfsCommand
    {
        private readonly Fetch _fetch;
        private readonly Globals _globals;
        private bool _shouldRebase;

        public OptionSet OptionSet
        {
            get
            {
                return _fetch.OptionSet
                            .Add("r|rebase", "Rebase your modifications on tfs changes", v => _shouldRebase = v != null);
            }
        }

        public Pull(Globals globals, Fetch fetch)
        {
            _fetch = fetch;
            _globals = globals;
        }

        public int Run()
        {
            return Run(_globals.RemoteId);
        }

        public int Run(string remoteId)
        {
            var retVal = _fetch.Run(remoteId);

            if (retVal == 0)
            {
                // TFS representations of repository paths do not have trailing slashes
                var tfsBranchPath = (remoteId ?? string.Empty).TrimEnd('/');

                if (!tfsBranchPath.IsValidTfsPath())
                {
                    var remotes = _globals.Repository.GetLastParentTfsCommits(tfsBranchPath);
                    if (!remotes.Any())
                    {
                        throw new Exception("error: TFS branch not found: " + remoteId);
                    }
                    tfsBranchPath = remotes.First().Remote.TfsRepositoryPath;
                }

                var allRemotes = _globals.Repository.ReadAllTfsRemotes();
                var remote = allRemotes.FirstOrDefault(r => String.Equals(r.TfsRepositoryPath, tfsBranchPath, StringComparison.OrdinalIgnoreCase));
                if (_shouldRebase)
                {
                    _globals.WarnOnGitVersion();

                    if (_globals.Repository.WorkingCopyHasUnstagedOrUncommitedChanges)
                    {
                        throw new GitTfsException("error: You have local changes; rebase-workflow only possible with clean working directory.")
                            .WithRecommendation("Try 'git stash' to stash your local changes and pull again.");
                    }
                    _globals.Repository.CommandNoisy("rebase", "--preserve-merges", remote.RemoteRef);
                }
                else
                    _globals.Repository.Merge(remote.RemoteRef);
            }

            return retVal;
        }
    }
}
