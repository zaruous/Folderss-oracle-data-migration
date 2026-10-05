using System;
using System.Windows;
using MigrationStudio.Services;

namespace MigrationStudio.Ui
{
    internal interface IMappingUiHost
    {
        Window Owner { get; }
        StudioState State { get; }
        ConnectionService Connections { get; }
        MappingOperations Operations { get; }
        void GoToStep(int step);
        void GoToColumns(string mappingId);
        void OpenSqlEditor(string mappingId);
        void OpenAddMapping(string sourceTable, bool sqlMode);
        void OpenAutoMatch();
        void OpenSettings();
        void ExportTemplate();
        void RunValidation();
    }
}
