using System;

namespace DeltaPatchGenerator
{ 
    [Serializable]
    public class Config
    {
        public string SourcePath { get; set; } = @"C:\Builds\Version1";
        public string TargetPath { get; set; } = @"C:\Builds\Version2";
        public string OutputPath { get; set; } = "Patch1-2";
        public bool VerifyResults { get; set; } = true;
        public bool UseCache { get; set; } = false;
        public string RejectionRegex { get; set; } = @"\.png$";
        public string XdeltaPath { get; set; } = @"xdelta3\xdelta3.exe";
        public string XdeltaLicensePath { get; set; } = @"xdelta3\LICENSE";
    }
}
