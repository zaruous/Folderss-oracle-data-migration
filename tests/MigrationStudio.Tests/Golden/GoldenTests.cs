using MigrationStudio.Tests;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MigrationStudio.Core.Expressions;
using MigrationStudio.Core.Jobs;
using MigrationStudio.Core.Mapping;
using MigrationStudio.Core.Metadata;
using MigrationStudio.Core.Model;
using MigrationStudio.Core.Settings;
using MigrationStudio.Core.Sql;
using MigrationStudio.Core.Types;
using Xunit;

namespace MigrationStudio.Tests.Golden
{
    public sealed class GoldenTests
    {
        private static GoldenDocument G => GoldenFixture.Document;

        public static IEnumerable<object[]> TypeCases()
        {
            foreach (var c in G.Types)
            {
                yield return new object[] { c.Input, c };
            }
        }

        [Theory]
        [MemberData(nameof(TypeCases))]
        public void Types_match_golden(string input, GoldenTypeCase expected)
        {
            var parsed = ExpressionTypeParse.Parse(input);
            Assert.NotNull(parsed);
            AssertEx.Equal(expected.Base, parsed.Base, "base for " + input);
            AssertEx.Equal(expected.Length, parsed.Len, "length for " + input);
            AssertEx.Equal(expected.Precision, parsed.Prec, "precision for " + input);
            AssertEx.Equal(expected.Scale, parsed.Scale, "scale for " + input);
            AssertEx.Equal(expected.IsChar, ExpressionTypeParse.IsChar(parsed), "isChar for " + input);
            AssertEx.Equal(expected.IsDate, ExpressionTypeParse.IsDate(parsed), "isDate for " + input);
        }

        public static IEnumerable<object[]> CompatCases()
        {
            foreach (var c in G.Compat)
            {
                yield return new object[] { c.Source + "→" + c.Target + (c.Stats?.MaxLength != null ? " maxLen=" + c.Stats.MaxLength : ""), c };
            }
        }

        [Theory]
        [MemberData(nameof(CompatCases))]
        public void Compat_match_golden(string label, GoldenCompatCase expected)
        {
            var result = MappingService.Compat(expected.Source, expected.Target, expected.Stats);
            AssertEx.Equal(expected.Result.Level, result.Level, label + " level");
            AssertEx.Equal(expected.Result.Kind, result.Kind, label + " kind");
            AssertEx.Equal(expected.Result.Message, result.Message, label + " msg");
        }

        public static IEnumerable<object[]> ExpressionCases()
        {
            foreach (var c in G.Expressions)
            {
                var preview = c.Expr;
                if (preview != null && preview.Length > 40)
                {
                    preview = preview.Substring(0, 40) + "…";
                }

                yield return new object[] { preview ?? "(null)", c };
            }
        }

        [Theory]
        [MemberData(nameof(ExpressionCases))]
        public void Expressions_match_golden(string label, GoldenExpressionCase expected)
        {
            var analysis = ExpressionAnalyzer.Analyze(expected.Expr);
            if (expected.Error == null)
            {
                AssertEx.Null(analysis.Error, label + " error");
            }
            else
            {
                AssertEx.NotNull(analysis.Error, label + " expected error");
                AssertEx.Equal(expected.Error.Code, analysis.Error.Code, label + " error.code");
                AssertEx.Equal(expected.Error.Message, analysis.Error.Message, label + " error.message");
                AssertEx.Equal(expected.Error.Position ?? -1, analysis.Error.Position, label + " error.position");
            }

            JsonAssert.EqualLists(expected.Refs ?? new List<string>(), analysis.Refs?.ToList() ?? new List<string>(), label + " refs");
            JsonAssert.EqualLists(expected.Binds ?? new List<string>(), analysis.Binds?.ToList() ?? new List<string>(), label + " binds");

            if (expected.Type == null)
            {
                if (analysis.Node != null)
                {
                    var columnType = GoldenTestHelpers.CustomerOrderColumnTypes(G.Source);
                    AssertEx.Null(TypeInference.Infer(analysis.Node, columnType), label + " type");
                }
            }
            else
            {
                AssertEx.NotNull(analysis.Node, label + " node for type");
                var columnType = GoldenTestHelpers.CustomerOrderColumnTypes(G.Source);
                AssertEx.Equal(expected.Type, TypeInference.Infer(analysis.Node, columnType), label + " type");
            }
        }

        [Fact]
        public void AutoMatchTables_match_golden()
        {
            var job = GoldenTestHelpers.SampleJob();
            var existing = GoldenTestHelpers.TableMappings(job).ToList();
            var actual = MappingService.AutoMatchTables(G.Source.Tables, G.Target.Tables, existing);
            AssertEx.Equal(G.AutoMatchTables.Count, actual.Count, "count");
            for (var i = 0; i < G.AutoMatchTables.Count; i++)
            {
                var exp = G.AutoMatchTables[i];
                AssertEx.Equal(exp.Source, actual[i].Source, "source[" + i + "]");
                AssertEx.Equal(exp.Target, actual[i].Target, "target[" + i + "]");
                AssertEx.Equal(exp.Reason, actual[i].Reason, "reason[" + i + "]");
            }
        }

        public static IEnumerable<object[]> AutoMapColumnCases()
        {
            foreach (var c in G.AutoMapColumns)
            {
                yield return new object[] { c.Source + "→" + c.Target, c };
            }
        }

        [Theory]
        [MemberData(nameof(AutoMapColumnCases))]
        public void AutoMapColumns_match_golden(string label, GoldenAutoMapCase expected)
        {
            var src = GoldenTestHelpers.FindSourceTable(G.Source, expected.Source);
            var tgt = GoldenTestHelpers.FindTargetTable(G.Target, expected.Target);
            var actual = MappingService.AutoMapColumns(src.Columns, tgt.Columns);
            AssertEx.Equal(expected.Columns.Count, actual.Count, label + " count");
            for (var i = 0; i < expected.Columns.Count; i++)
            {
                var exp = expected.Columns[i];
                var col = actual[i].Mapping;
                AssertEx.Equal(exp.Target, col.Target, label + " target[" + i + "]");
                AssertEx.Equal(exp.Source, col.Source, label + " source[" + i + "]");
                AssertEx.Equal(exp.Expr ?? "", col.Expr ?? "", label + " expr[" + i + "]");
                AssertEx.Equal(exp.NullRule, col.NullRule, label + " nullRule[" + i + "]");
                AssertEx.Equal(exp.DefaultValue ?? "", col.DefaultValue ?? "", label + " defaultValue[" + i + "]");
                AssertEx.Equal(exp.Reason, actual[i].Reason, label + " reason[" + i + "]");
            }
        }

        [Fact]
        public void SampleJob_serializes_like_golden()
        {
            var settings = GoldenTestHelpers.SettingsFromGolden();
            var json = JobFile.Serialize(GoldenTestHelpers.SampleJob(), settings);
            JsonAssert.EqualJson(G.SampleJob.GetRawText(), json, "sampleJob");
        }

        [Fact]
        public void SampleJob_parses_from_golden()
        {
            var job = JobFile.Parse(G.SampleJob.GetRawText());
            AssertEx.Equal(2, job.Version);
            AssertEx.Equal("CUSTOMER_MIGRATION", job.JobName);
            AssertEx.Equal(4, job.Mappings.Count);
            Assert.True(job.Checkpoints.ContainsKey("tm-customer"));
            AssertEx.Equal("850000", job.Checkpoints["tm-customer"].Value);
        }

        public static IEnumerable<object[]> MappingStatusCases()
        {
            foreach (var c in G.MappingStatus)
            {
                yield return new object[] { c.Mapping, c };
            }
        }

        [Theory]
        [MemberData(nameof(MappingStatusCases))]
        public void MappingStatus_match_golden(string mappingId, GoldenMappingStatusCase expected)
        {
            var job = GoldenTestHelpers.SampleJob();
            var mapping = job.FindMapping(mappingId);
            Assert.NotNull(mapping);
            var src = GoldenTestHelpers.FindSourceTable(G.Source, mapping.Source);
            var tgt = GoldenTestHelpers.FindTargetTable(G.Target, mapping.Target);
            var st = MappingService.Status(mapping, src, tgt);
            AssertEx.Equal(expected.Mapped, st.Mapped, mappingId + " mapped");
            AssertEx.Equal(expected.Total, st.Total, mappingId + " total");
            AssertEx.Equal(expected.Level, st.Level, mappingId + " level");
            AssertEx.Equal(expected.Errors, st.Errors, mappingId + " errors");
            AssertEx.Equal(expected.Warns, st.Warns, mappingId + " warns");
            AssertEx.Equal(expected.Columns.Count, st.Results.Count, mappingId + " columns count");
            for (var i = 0; i < expected.Columns.Count; i++)
            {
                var expCol = expected.Columns[i];
                var res = st.Results[i];
                AssertEx.Equal(expCol.Target, res.Target.Name, mappingId + " col[" + i + "] target");
                AssertEx.Equal(expCol.Level, res.Check.Level, mappingId + " col[" + i + "] level");
                AssertEx.Equal(expCol.Type, res.Check.Type, mappingId + " col[" + i + "] type");
                AssertEx.Equal(expCol.Msgs.Count, res.Check.Messages.Count, mappingId + " col[" + i + "] msgs count");
                for (var j = 0; j < expCol.Msgs.Count; j++)
                {
                    AssertEx.Equal(expCol.Msgs[j].Level, res.Check.Messages[j].Level, mappingId + " col[" + i + "] msg[" + j + "] level");
                    AssertEx.Equal(expCol.Msgs[j].Message, res.Check.Messages[j].Message, mappingId + " col[" + i + "] msg[" + j + "] msg");
                }
            }
        }

        public static IEnumerable<object[]> ParseSelectCases()
        {
            foreach (var c in G.ParseSelect)
            {
                yield return new object[] { c.Key, c };
            }
        }

        [Theory]
        [MemberData(nameof(ParseSelectCases))]
        public void ParseSelect_match_golden(string key, GoldenParseSelectCase expected)
        {
            var parsed = SelectParser.Parse(expected.Sql);
            AssertEx.Equal(expected.Errors?.Count ?? 0, parsed.Errors.Count, key + " errors count");
            for (var i = 0; i < (expected.Errors?.Count ?? 0); i++)
            {
                AssertEx.Equal(expected.Errors[i].Code, parsed.Errors[i].Code, key + " error[" + i + "] code");
                AssertEx.Equal(expected.Errors[i].Message, parsed.Errors[i].Message, key + " error[" + i + "] msg");
                AssertEx.Equal(expected.Errors[i].Line, parsed.Errors[i].Line, key + " error[" + i + "] line");
            }

            JsonAssert.EqualLists(expected.Warnings ?? new List<string>(), parsed.Warnings, key + " warnings");
            AssertEx.Equal(expected.Items.Count, parsed.Items.Count, key + " items count");
            for (var i = 0; i < expected.Items.Count; i++)
            {
                var exp = expected.Items[i];
                var item = parsed.Items[i];
                AssertEx.Equal(exp.Name, item.Name, key + " item[" + i + "] name");
                AssertEx.Equal(exp.Alias, item.Alias, key + " item[" + i + "] alias");
                AssertEx.Equal(exp.Expr, item.Expr, key + " item[" + i + "] expr");
                AssertEx.Equal(exp.Star, item.Star, key + " item[" + i + "] star");
                if (exp.Error == null)
                {
                    AssertEx.Null(item.Error, key + " item[" + i + "] error");
                }
                else
                {
                    AssertEx.NotNull(item.Error, key + " item[" + i + "] error");
                    AssertEx.Equal(exp.Error, item.Error.Code, key + " item[" + i + "] error code");
                }
            }

            AssertEx.Equal(expected.Tables.Count, parsed.Tables.Count, key + " tables count");
            for (var i = 0; i < expected.Tables.Count; i++)
            {
                var expTable = expected.Tables[i];
                var actTable = parsed.Tables[i];
                if (expTable.TryGetProperty("subquery", out var sub) && sub.ValueKind == JsonValueKind.True)
                {
                    Assert.True(actTable.Subquery, key + " table[" + i + "] subquery");
                    continue;
                }

                Assert.False(actTable.Subquery, key + " table[" + i + "] subquery");
                AssertEx.Equal(GetString(expTable, "schema"), actTable.Schema, key + " table[" + i + "] schema");
                AssertEx.Equal(GetString(expTable, "name"), actTable.Name, key + " table[" + i + "] name");
                AssertEx.Equal(GetString(expTable, "alias"), actTable.Alias, key + " table[" + i + "] alias");
                AssertEx.Equal(GetString(expTable, "join"), actTable.Join, key + " table[" + i + "] join");
                var on = GetString(expTable, "on");
                AssertEx.Equal(on, actTable.On, key + " table[" + i + "] on");
            }

            JsonAssert.EqualLists(expected.Binds ?? new List<string>(), parsed.Binds, key + " binds");
            if (expected.Where == null)
            {
                Assert.True(parsed.Where == null || string.IsNullOrEmpty(parsed.Where.Text), key + " where");
            }
            else
            {
                AssertEx.Equal(expected.Where, parsed.Where?.Text, key + " where");
            }

            AssertEx.Equal(expected.OrderBy, parsed.OrderBy, key + " orderBy");
            AssertEx.Equal(expected.GroupBy, parsed.GroupBy, key + " groupBy");
        }

        public static IEnumerable<object[]> DescribeCases()
        {
            foreach (var c in G.Describe)
            {
                yield return new object[] { c.Key, c };
            }
        }

        [Theory]
        [MemberData(nameof(DescribeCases))]
        public void Describe_match_golden(string key, GoldenDescribeCase expected)
        {
            var sql = GoldenTestHelpers.ParseSelectSql(G, key);
            var statement = SelectParser.Parse(sql);
            var described = SqlSourceAnalyzer.Describe(statement, G.Source);
            AssertEx.Equal(expected.Errors?.Count ?? 0, described.Errors.Count, key + " errors count");
            for (var i = 0; i < (expected.Errors?.Count ?? 0); i++)
            {
                AssertEx.Equal(expected.Errors[i].Code, described.Errors[i].Code, key + " error[" + i + "] code");
                AssertEx.Equal(expected.Errors[i].Message, described.Errors[i].Message, key + " error[" + i + "] msg");
            }

            AssertEx.Equal(expected.Columns.Count, described.Columns.Count, key + " columns count");
            for (var i = 0; i < expected.Columns.Count; i++)
            {
                var exp = expected.Columns[i];
                var col = described.Columns[i];
                AssertEx.Equal(exp.Name, col.Name, key + " col[" + i + "] name");
                AssertEx.Equal(exp.Type, col.Type, key + " col[" + i + "] type");
                AssertEx.Equal(exp.Expr, col.Expr, key + " col[" + i + "] expr");
                AssertEx.Equal(exp.Nullable, col.Nullable, key + " col[" + i + "] nullable");
                AssertEx.Equal(exp.Error, col.Error, key + " col[" + i + "] error");
                if (exp.Stats == null)
                {
                    continue;
                }

                AssertEx.Equal(exp.Stats.Nulls, col.Stats?.Nulls, key + " col[" + i + "] stats.nulls");
                AssertEx.Equal(exp.Stats.Blanks, col.Stats?.Blanks, key + " col[" + i + "] stats.blanks");
                AssertEx.Equal(exp.Stats.MaxLength, col.Stats?.MaxLength, key + " col[" + i + "] stats.maxLength");
            }
        }

        [Fact]
        public void VirtualSource_match_golden()
        {
            var job = GoldenTestHelpers.SampleJob();
            var mapping = job.FindMapping(G.VirtualSource.Mapping);
            var info = SqlSourceAnalyzer.Analyze(mapping, G.Source);
            var table = info.Table;
            AssertEx.Equal(G.VirtualSource.Name, table.Name);
            AssertEx.Equal(G.VirtualSource.Rows, table.Rows);
            AssertEx.Equal(G.VirtualSource.BaseRows, info.BaseRows);
            AssertEx.Equal(G.VirtualSource.Comment, table.Comment);
            AssertEx.Equal(G.VirtualSource.Columns.Count, table.Columns.Count);
            for (var i = 0; i < G.VirtualSource.Columns.Count; i++)
            {
                var exp = G.VirtualSource.Columns[i];
                var col = table.Columns[i];
                AssertEx.Equal(exp.Name, col.Name, "col[" + i + "] name");
                AssertEx.Equal(exp.Type, col.Type, "col[" + i + "] type");
                AssertEx.Equal(exp.Nullable, col.Nullable, "col[" + i + "] nullable");
                AssertEx.Equal(exp.Comment, col.Comment, "col[" + i + "] comment");
            }
        }

        public static IEnumerable<object[]> SqlValidateCases()
        {
            foreach (var c in G.SqlValidate)
            {
                yield return new object[] { c.Key, c };
            }
        }

        [Theory]
        [MemberData(nameof(SqlValidateCases))]
        public void SqlValidate_match_golden(string key, GoldenSqlValidateCase expected)
        {
            var job = GoldenTestHelpers.SampleJob();
            var mapping = GoldenTestHelpers.BuildSqlValidateMapping(G, job, key);
            var result = SqlSourceAnalyzer.Validate(mapping, G.Source, G.Target);
            AssertEx.Equal(expected.Level, result.Level, key + " level");
            AssertEx.Equal(expected.Mapped, result.Mapped, key + " mapped");
            AssertEx.Equal(expected.Total, result.Total, key + " total");
            AssertEx.Equal(expected.ErrorLine, result.ErrorLine, key + " errorLine");
            AssertEx.Equal(expected.Items.Count, result.Items.Count, key + " items count");
            for (var i = 0; i < expected.Items.Count; i++)
            {
                AssertEx.Equal(expected.Items[i].Check, result.Items[i].Check, key + " item[" + i + "] check");
                AssertEx.Equal(expected.Items[i].Level, result.Items[i].Level, key + " item[" + i + "] level");
                AssertEx.Equal(expected.Items[i].Detail, result.Items[i].Detail, key + " item[" + i + "] detail");
            }
        }

        [Fact]
        public void Sqlgen_sourceSelectTableWorkers4_match_golden()
        {
            var job = GoldenTestHelpers.SampleJob();
            var tmC = job.FindMapping("tm-customer");
            var src = GoldenTestHelpers.FindSourceTable(G.Source, "SRC_CUSTOMER");
            var tgt = GoldenTestHelpers.FindTargetTable(G.Target, "TB_MEMBER");
            var sql = SqlGenerator.BuildSourceSelect(tmC, "LEGACY_APP", src, tgt, new SourceSelectOptions { FetchSize = 5000, Workers = 4 });
            AssertEx.Equal(G.Sqlgen.SourceSelectTableWorkers4, sql);
        }

        [Fact]
        public void Sqlgen_sourceSelectTableWorkers1Where_match_golden()
        {
            var job = GoldenTestHelpers.SampleJob();
            var tmO = job.FindMapping("tm-order");
            var src = GoldenTestHelpers.FindSourceTable(G.Source, "SRC_ORDER");
            var tgt = GoldenTestHelpers.FindTargetTable(G.Target, "TB_SALES_ORDER");
            var sql = SqlGenerator.BuildSourceSelect(tmO, "LEGACY_APP", src, tgt, new SourceSelectOptions { FetchSize = 5000, Workers = 1 });
            AssertEx.Equal(G.Sqlgen.SourceSelectTableWorkers1Where, sql);
        }

        [Fact]
        public void Sqlgen_sourceSelectSql_match_golden()
        {
            var job = GoldenTestHelpers.SampleJob();
            var sm = GoldenTestHelpers.SqlSampleMapping(job);
            var v = SqlSourceAnalyzer.Analyze(sm, G.Source).Table;
            var tgt = GoldenTestHelpers.FindTargetTable(G.Target, "TB_MEMBER");
            var sql = SqlGenerator.BuildSourceSelect(sm, "LEGACY_APP", v, tgt, new SourceSelectOptions { FetchSize = 5000, Workers = 4 });
            AssertEx.Equal(G.Sqlgen.SourceSelectSql, sql);
        }

        [Fact]
        public void Sqlgen_writeColumnsCustomer_match_golden()
        {
            var job = GoldenTestHelpers.SampleJob();
            var tmC = job.FindMapping("tm-customer");
            var tgt = GoldenTestHelpers.FindTargetTable(G.Target, "TB_MEMBER");
            var cols = SqlGenerator.WriteColumns(tmC, tgt);
            AssertEx.Equal(G.Sqlgen.WriteColumnsCustomer.Count, cols.Count);
            for (var i = 0; i < G.Sqlgen.WriteColumnsCustomer.Count; i++)
            {
                AssertEx.Equal(G.Sqlgen.WriteColumnsCustomer[i].Name, cols[i].Name, "name[" + i + "]");
                AssertEx.Equal(G.Sqlgen.WriteColumnsCustomer[i].Expr, cols[i].Expr, "expr[" + i + "]");
            }
        }

        public static IEnumerable<object[]> SqlgenWriteCases()
        {
            foreach (var pair in G.Sqlgen.Write)
            {
                yield return new object[] { pair.Key, pair.Value };
            }
        }

        [Theory]
        [MemberData(nameof(SqlgenWriteCases))]
        public void Sqlgen_write_match_golden(string modeKey, string expectedSql)
        {
            var job = GoldenTestHelpers.SampleJob();
            var tmC = job.FindMapping("tm-customer");
            var tgt = GoldenTestHelpers.FindTargetTable(G.Target, "TB_MEMBER");
            var colNames = SqlGenerator.WriteColumns(tmC, tgt).Select(c => c.Name).ToList();
            const string suffix = "_noErrorTable_noKey";
            var noError = modeKey.EndsWith(suffix, StringComparison.Ordinal);
            var mode = noError ? modeKey.Substring(0, modeKey.Length - suffix.Length) : modeKey;
            var keys = noError ? new List<string>() : new List<string> { "MEMBER_ID" };
            var errorTable = noError ? null : "ERR$_TB_MEMBER";
            var sql = SqlGenerator.BuildWriteSql("NEXT_APP", "TB_MEMBER", colNames, mode, keys, errorTable);
            AssertEx.Equal(expectedSql, sql);
        }

        public static IEnumerable<object[]> SqlgenLiteralCases()
        {
            foreach (var c in G.Sqlgen.Literal)
            {
                yield return new object[] { (c.Value ?? "null") + " / " + c.Type, c };
            }
        }

        [Theory]
        [MemberData(nameof(SqlgenLiteralCases))]
        public void Sqlgen_literal_match_golden(string label, GoldenLiteralCase expected)
        {
            AssertEx.Equal(expected.Result, SqlGenerator.Literal(expected.Value, expected.Type), label);
        }

        public static IEnumerable<object[]> SqlgenValueExprCases()
        {
            foreach (var c in G.Sqlgen.ValueExpr)
            {
                yield return new object[] { c.Rule + " " + (c.Source ?? "null"), c };
            }
        }

        [Theory]
        [MemberData(nameof(SqlgenValueExprCases))]
        public void Sqlgen_valueExpr_match_golden(string label, GoldenValueExprCase expected)
        {
            var tgt = GoldenTestHelpers.FindTargetTable(G.Target, "TB_MEMBER");
            ColumnMetadata col;
            if (expected.TargetType == "TIMESTAMP")
            {
                col = tgt.FindColumn("CREATED_AT");
            }
            else
            {
                col = tgt.FindColumn("USE_YN");
            }

            var mapping = new ColumnMapping
            {
                Source = expected.Source,
                Expr = expected.Expr ?? "",
                NullRule = expected.Rule,
                DefaultValue = expected.DefaultValue ?? ""
            };
            AssertEx.Equal(expected.Result, SqlGenerator.ValueExpression(mapping, col), label);
        }

        public static IEnumerable<object[]> SqlgenErrorTableCases()
        {
            foreach (var c in G.Sqlgen.ErrorTableFor)
            {
                yield return new object[] { c.Strategy.ErrorPolicy + "/" + c.Strategy.ErrorTable, c };
            }
        }

        [Theory]
        [MemberData(nameof(SqlgenErrorTableCases))]
        public void Sqlgen_errorTableFor_match_golden(string label, GoldenErrorTableCase expected)
        {
            var strategy = new MigrationStrategy
            {
                ErrorPolicy = expected.Strategy.ErrorPolicy,
                ErrorTable = expected.Strategy.ErrorTable
            };
            var result = SqlGenerator.ErrorTableFor(strategy, expected.Target);
            AssertEx.Equal(expected.Result, result, label);
        }

        [Fact]
        public void JobV1_upgrades_like_golden()
        {
            var job = JobFile.Parse(G.JobV1.GetRawText());
            var exp = G.JobV1Upgraded;
            AssertEx.Equal(exp.Version, job.Version);
            AssertConnection(exp.Source, job.Source);
            AssertConnection(exp.Target, job.Target);
            AssertEx.Equal(exp.Mappings.Count, job.Mappings.Count);
            for (var i = 0; i < exp.Mappings.Count; i++)
            {
                var em = exp.Mappings[i];
                var am = job.Mappings[i];
                AssertEx.Equal(em.Id, am.Id, "mapping[" + i + "] id");
                AssertEx.Equal(em.Use, am.Use, "mapping[" + i + "] use");
                AssertEx.Equal(em.SourceType, am.SourceType, "mapping[" + i + "] sourceType");
                AssertEx.Equal(em.Source, am.Source, "mapping[" + i + "] source");
                AssertEx.Equal(em.Sql, am.Sql, "mapping[" + i + "] sql");
                AssertEx.Equal(em.Target, am.Target, "mapping[" + i + "] target");
                AssertEx.Equal(em.Mode, am.Mode, "mapping[" + i + "] mode");
                JsonAssert.EqualLists(em.MergeKey, am.MergeKey, "mapping[" + i + "] mergeKey");
                AssertEx.Equal(em.CheckpointColumn, am.CheckpointColumn, "mapping[" + i + "] checkpointColumn");
                AssertEx.Equal(em.Columns.Count, am.Columns.Count, "mapping[" + i + "] columns");
                for (var j = 0; j < em.Columns.Count; j++)
                {
                    AssertEx.Equal(em.Columns[j].Target, am.Columns[j].Target, "mapping[" + i + "] col[" + j + "] target");
                    AssertEx.Equal(em.Columns[j].Source, am.Columns[j].Source, "mapping[" + i + "] col[" + j + "] source");
                    AssertEx.Equal(em.Columns[j].Expr ?? "", am.Columns[j].Expr ?? "", "mapping[" + i + "] col[" + j + "] expr");
                    AssertEx.Equal(em.Columns[j].NullRule, am.Columns[j].NullRule, "mapping[" + i + "] col[" + j + "] nullRule");
                    AssertEx.Equal(em.Columns[j].DefaultValue ?? "", am.Columns[j].DefaultValue ?? "", "mapping[" + i + "] col[" + j + "] defaultValue");
                }
            }

            foreach (var pair in exp.Checkpoints)
            {
                Assert.True(job.Checkpoints.ContainsKey(pair.Key), "checkpoint " + pair.Key);
                var actualCp = job.Checkpoints[pair.Key];
                var cpEl = pair.Value;
                AssertEx.Equal(GetString(cpEl, "column"), actualCp.Column, pair.Key + " column");
                AssertEx.Equal(ReadFlexibleValue(cpEl, "value"), actualCp.Value, pair.Key + " value");
                AssertEx.Equal(GetInt64(cpEl, "rows"), actualCp.Rows, pair.Key + " rows");
                AssertEx.Equal(GetInt64(cpEl, "total"), actualCp.Total, pair.Key + " total");
                AssertEx.Equal(GetString(cpEl, "at"), actualCp.At, pair.Key + " at");
                AssertEx.Equal(GetString(cpEl, "runId"), actualCp.RunId, pair.Key + " runId");
                AssertEx.Equal(GetString(cpEl, "status"), actualCp.Status, pair.Key + " status");
            }
        }

        [Fact]
        public void YamlSample_match_golden()
        {
            var settings = GoldenTestHelpers.SettingsFromGolden();
            var yaml = JobFile.ToYaml(GoldenTestHelpers.SampleJob(), settings);
            AssertEx.Equal(G.YamlSample, yaml);
        }

        [Fact]
        public void SettingsDefaults_match_product_defaults()
        {
            var product = MigrationSettingsStore.CreateDefaultSettings();
            var golden = G.SettingsDefaults;
            AssertEx.Equal(golden.Defaults.CommitSize, product.Defaults.CommitSize);
            AssertEx.Equal(golden.Defaults.FetchSize, product.Defaults.FetchSize);
            AssertEx.Equal(golden.Defaults.Workers, product.Defaults.Workers);
            AssertEx.Equal(golden.Defaults.ErrorPolicy, product.Defaults.ErrorPolicy);
            AssertEx.Equal(golden.Defaults.ErrorTable, product.Defaults.ErrorTable);
            AssertEx.Equal(golden.Defaults.CheckpointStore, product.Defaults.CheckpointStore);
            AssertEx.Equal(golden.Defaults.ControlPrefix, product.Defaults.ControlPrefix);
            AssertEx.Equal(golden.Agent.OnHostExit, product.Agent.OnHostExit);
            AssertEx.Equal(golden.Agent.MaxConcurrent, product.Agent.MaxConcurrent);
            AssertEx.Equal(golden.Agent.LogDays, product.Agent.LogDays);
        }

        private static void AssertConnection(GoldenConnectionRef expected, ConnectionRef actual)
        {
            AssertEx.Equal(expected.ProfileId, actual.ProfileId);
            AssertEx.Equal(expected.Schema, actual.Schema);
            AssertEx.Equal(expected.Name, actual.Name);
            AssertEx.Equal(expected.Kind, actual.Kind);
            AssertEx.Equal(expected.Host, actual.Host);
            AssertEx.Equal(expected.Port, actual.Port);
            AssertEx.Equal(expected.Service, actual.Service);
            AssertEx.Equal(expected.User, actual.User);
            AssertEx.Equal(expected.Color, actual.Color);
        }

        private static string GetString(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        }

        private static long GetInt64(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            {
                return 0;
            }

            return v.GetInt64();
        }

        private static string ReadFlexibleValue(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v))
            {
                return null;
            }

            if (v.ValueKind == JsonValueKind.Number)
            {
                return v.GetRawText();
            }

            if (v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }

            return null;
        }
    }
}
