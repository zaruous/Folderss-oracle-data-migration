using System;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Core.Storage
{
    public sealed class JobDraft
    {
        public DateTime SavedAt { get; set; }
        public string FilePath { get; set; }
        public bool Dirty { get; set; }
        public MigrationJob Job { get; set; }
    }
}
