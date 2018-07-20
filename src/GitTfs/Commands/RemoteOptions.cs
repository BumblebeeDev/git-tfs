using NDesk.Options;
using GitTfs.Util;

namespace GitTfs.Commands
{
    [StructureMapSingleton]
    public class RemoteOptions
    {
        public OptionSet OptionSet
        {
            get
            {
                return new OptionSet
                {
                    { "ignore-regex=", "A regex of files to ignore",
                        v => IgnoreRegex = v },
                    { "except-regex=", "A regex of exceptions to '--ignore-regex'",
                        v => ExceptRegex = v},
                    { "u|username=", "TFS username",
                        v => Username = v },
                    { "p|password=", "TFS password",
                        v => Password = v },
                    { "cut-path=", "Cut from the start of the TFS path",
                        v => CutPath = v },
                    { "cut-path-force", "Do not stop with error if some path do not start with specified --cut-path",
                        v => CutPathForce = (v != null) },
                };
            }
        }

        public string IgnoreRegex { get; set; }
        public string ExceptRegex { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string CutPath { get; set; }
        public bool CutPathForce { get; set; }
    }
}
