using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Logic;
using Xunit;

namespace MigrationStudio.Tests
{
    public sealed class MappingUiLogicTests
    {
        [Fact]
        public void TablesLogic_RowLevel_SqlWithoutColumns_IsError()
        {
            var m = new Mapping { SourceType = SourceTypes.Sql, Source = "SQLMAP_X", Target = "T" };
            var src = new TableMetadata { Kind = "SQL", Columns = new System.Collections.Generic.List<ColumnMetadata>(), Comment = "ORA-00942" };
            var model = TablesLogic.RowModel(m, src, new TableMetadata { Name = "T", Columns = new System.Collections.Generic.List<ColumnMetadata>() },
                new ConnectionProfile(), MappingService.Status(m, null, new TableMetadata()));
            Assert.Equal(CheckLevels.Error, model.Level);
            Assert.True(model.SourceBad);
        }

        [Fact]
        public void TablesLogic_DestructiveWarn_RedTarget()
        {
            var m = new Mapping { Mode = WriteModes.TruncateInsert, Target = "T" };
            var model = TablesLogic.RowModel(m, new TableMetadata(), new TableMetadata(),
                new ConnectionProfile { Color = "red" },
                new MappingStatus { Level = CheckLevels.Pass, Mapped = 1, Total = 1 });
            Assert.True(model.ShowDestructiveWarn);
        }

        [Fact]
        public void ColumnsLogic_SourceChange_ReplacesWordBoundary()
        {
            var cm = new ColumnMapping { Target = "X", Source = "OLD_COL", Expr = "TRIM(OLD_COL)" };
            ColumnsLogic.ApplySourceColumnChange(cm, "OLD_COL", "NEW_COL",
                new ColumnMetadata { Name = "NEW_COL" },
                new ColumnMetadata { Name = "X" });
            Assert.Equal("NEW_COL", cm.Source);
            Assert.Equal("TRIM(NEW_COL)", cm.Expr);
        }

        [Fact]
        public void ColumnsLogic_Snippet_Trim()
        {
            Assert.Equal("TRIM(COL)", ColumnsLogic.WrapSnippet("TRIM", "COL"));
        }

        [Fact]
        public void SqlEditorLogic_LineStyles_MarksErrorLine()
        {
            var styles = SqlEditorLogic.LineStyles("SELECT 1\nFROM T", new Core.Sql.SqlValidationResult { ErrorLine = 2 });
            Assert.Equal(2, styles.Count);
            Assert.True(styles[1].IsError);
        }
    }
}
