using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GitTfs.Core;
using System.Diagnostics;

namespace GitTfs.Util
{
    [StructureMapSingleton]
    public class BranchParentsFile
    {
        private readonly Dictionary<string, int> _branchesParents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public bool IsParseSuccessfull { get; set; }

        public bool NeedCopy { get; set; }

        public static string GitTfsCachedBranchParentsFileName = "git-tfs_branch-parents";

        public Dictionary<string, int> BranchParents
        {
            get
            {
                return _branchesParents;
            }
        }

        public int? FindBranchParent(string branchName)
        {
            int parent;
            return _branchesParents.TryGetValue(branchName, out parent) ? parent : (int?)null;
        }


        // The first time a tfs user id or a git id is encountered, it is used as lookup key.
        public bool Parse(TextReader branchParentsFileStream)
        {
            if (branchParentsFileStream == null)
                return false;

            _branchesParents.Clear();
            int lineCount = 0;
            string line = branchParentsFileStream.ReadLine();
            while (line != null)
            {
                lineCount++;
                if (!line.StartsWith("#"))
                {
                    Regex ex = new Regex(@"^(.+?)\s*=\s*(.+?)\s*$");
                    Match match = ex.Match(line);
                    int parent;
                    if (match.Groups.Count != 3 || string.IsNullOrWhiteSpace(match.Groups[1].Value) || string.IsNullOrWhiteSpace(match.Groups[2].Value) ||
                        !int.TryParse(match.Groups[2].Value, out parent))
                    {
                        throw new GitTfsException("Invalid format of branch parents file on line " + lineCount + ".");
                    }
                    var branchName = match.Groups[1].Value.Trim();

                    if (!_branchesParents.ContainsKey(branchName))
                        _branchesParents.Add(branchName, parent);
                }
                line = branchParentsFileStream.ReadLine();
            }
            IsParseSuccessfull = true;
            return true;
        }

        public void Parse(string branchParentsFilePath, string gitDir)
        {
            var savedBranchParentFile = Path.Combine(gitDir, GitTfsCachedBranchParentsFileName);
            if (!string.IsNullOrWhiteSpace(branchParentsFilePath))
            {
                if (!File.Exists(branchParentsFilePath))
                {
                    throw new GitTfsException("Branch parents file cannot be found: '" + branchParentsFilePath + "'");
                }
                Trace.WriteLine("Reading branch parents file : " + branchParentsFilePath);
                using (StreamReader sr = new StreamReader(branchParentsFilePath))
                {
                    Parse(sr);
                }
                NeedCopy = true;
                return;
            }
            if (File.Exists(savedBranchParentFile))
            {
                if (BranchParents.Count != 0)
                    return;
                Trace.WriteLine("Reading cached branch parents file (" + savedBranchParentFile + ")...");
                using (StreamReader sr = new StreamReader(savedBranchParentFile))
                {
                    Parse(sr);
                }
            }
            else
                Trace.WriteLine("No branch parents file used.");
        }

        public void CopyBranchParents(string branchParentsFilePath, string gitDir)
        {
            if (!NeedCopy) return;

            var savedBranchParentFile = Path.Combine(gitDir, GitTfsCachedBranchParentsFileName);
            try
            {
                var directoryName = Path.GetDirectoryName(savedBranchParentFile);
                if (directoryName != null) Directory.CreateDirectory(directoryName);
                File.Copy(branchParentsFilePath, savedBranchParentFile, true);
            }
            catch (Exception)
            {
                Trace.TraceWarning("Failed to copy branch parents file from \"" + branchParentsFilePath + "\" to \"" + savedBranchParentFile + "\".");
            }
        }
    }
}
