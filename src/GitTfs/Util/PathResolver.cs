using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GitTfs.Core;

namespace GitTfs.Util
{
    public class PathResolver
    {
        private readonly IGitTfsRemote _remote;
        private readonly string _cutPath;
        private readonly bool _cutPathForce;
        private readonly string _relativePath;
        private readonly IDictionary<string, GitObject> _initialTree;

        public PathResolver(IGitTfsRemote remote, string cutPath, bool cutPathForce, string relativePath, IDictionary<string, GitObject> initialTree)
        {
            _remote = remote;
            _cutPath = cutPath;
            _cutPathForce = cutPathForce;
            _relativePath = relativePath;
            _initialTree = initialTree;
        }

        public string GetPathInGitRepo(string tfsPath)
        {
            return GetGitObject(tfsPath).Try(x => x.Path);
        }

        public GitObject GetGitObject(string tfsPath)
        {
            var pathInGitRepo = _remote.GetPathInGitRepo(tfsPath);
            if (pathInGitRepo == null)
                return null;
            if (!string.IsNullOrEmpty(_cutPath))
            {
                if (!pathInGitRepo.StartsWith(_cutPath) && pathInGitRepo != String.Empty)
                {
                    if (!_cutPathForce)
                    {
                        throw new GitTfsException("error: found path that does not start with '" + _cutPath + "'" +
                                                  " and cannot be rebased to the root of the repository: '" + pathInGitRepo + "'.", new[]
                            {
                                "Reconsider the use of the '--cut-path' option",
                                "Specify '--cut-path-force' option to issue warnings in such cases instead of errors, leaving path of such files untouched",
                            }
                        );
                    }
                    Trace.TraceInformation("warning: found path that does not start with '" + _cutPath + "'" +
                                           " and cannot be rebased to the root of the repository: '" + pathInGitRepo + "'.");
                }
                if (pathInGitRepo.StartsWith(_cutPath + "/", StringComparison.Ordinal) || pathInGitRepo.Equals(_cutPath, StringComparison.Ordinal))
                    pathInGitRepo = pathInGitRepo.Substring(_cutPath.Length);
                while (pathInGitRepo.StartsWith("/"))
                    pathInGitRepo = pathInGitRepo.Substring(1);
            }
            if (!string.IsNullOrEmpty(_relativePath))
                pathInGitRepo = _relativePath + "/" + pathInGitRepo;
            return Lookup(pathInGitRepo);
        }

        public bool ShouldIncludeGitItem(string gitPath)
        {
            return !string.IsNullOrEmpty(gitPath) && !_remote.ShouldSkip(gitPath);
        }

        public bool Contains(string pathInGitRepo)
        {
            if (pathInGitRepo != null)
            {
                GitObject result;
                if (_initialTree.TryGetValue(pathInGitRepo, out result))
                    return result.Commit != null;
            }
            return false;
        }

        private static readonly Regex SplitDirnameFilename = new Regex(@"(?<dir>.*)[/\\](?<file>[^/\\]+)", RegexOptions.Compiled);

        private GitObject Lookup(string pathInGitRepo)
        {
            GitObject result;
            if (_initialTree.TryGetValue(pathInGitRepo, out result))
                return result;

            var fullPath = pathInGitRepo;
            var splitResult = SplitDirnameFilename.Match(pathInGitRepo);
            if (splitResult.Success)
            {
                var dirName = splitResult.Groups["dir"].Value;
                var fileName = splitResult.Groups["file"].Value;
                fullPath = Lookup(dirName).Path + "/" + fileName;
            }
            result = new GitObject { Path = fullPath };
            _initialTree[fullPath] = result;
            return result;
        }
    }
}
