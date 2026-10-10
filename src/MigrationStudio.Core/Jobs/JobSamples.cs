using System;
using System.Collections.Generic;
using MigrationStudio.Core.Model;

namespace MigrationStudio.Core.Jobs
{
    /// <summary>POC job.js sampleJob · SAMPLE_SQL — 테스트·골든 픽스처용.</summary>
    public static class JobSamples
    {
        public const string SampleSql =
            "SELECT\n" +
            "    C.CUSTOMER_ID AS MEMBER_ID,\n" +
            "\n" +
            "    TRIM(C.CUSTOMER_NM)\n" +
            "        AS MEMBER_NAME,\n" +
            "\n" +
            "    REGEXP_REPLACE(\n" +
            "        C.PHONE_NO,\n" +
            "        '[^0-9]',\n" +
            "        ''\n" +
            "    ) AS MOBILE_NO,\n" +
            "\n" +
            "    CASE\n" +
            "        WHEN C.STATUS_CD = 'A'\n" +
            "        THEN 'Y'\n" +
            "        ELSE 'N'\n" +
            "    END AS USE_YN,\n" +
            "\n" +
            "    G.GRADE_NM AS MEMBER_GRADE,\n" +
            "\n" +
            "    CAST(\n" +
            "        C.REG_DT AS TIMESTAMP\n" +
            "    ) AS CREATED_AT,\n" +
            "\n" +
            "    CAST(\n" +
            "        NVL(C.MOD_DT, C.REG_DT)\n" +
            "        AS TIMESTAMP\n" +
            "    ) AS UPDATED_AT\n" +
            "\n" +
            "FROM SRC_CUSTOMER C\n" +
            "\n" +
            "LEFT JOIN SRC_CUSTOMER_GRADE G\n" +
            "       ON G.GRADE_CD = C.GRADE_CD\n" +
            "\n" +
            "WHERE C.CUSTOMER_ID > :LAST_ID";

        /// <summary>POC <c>job.js</c> <c>SAMPLE_SQL</c>과 동일.</summary>
        public const string SAMPLE_SQL = SampleSql;

        public static MigrationJob SampleJob()
        {
            var job = new MigrationJob
            {
                FormatName = MigrationJob.Format,
                Version = MigrationJob.CurrentVersion,
                JobName = "CUSTOMER_MIGRATION",
                Description = "레거시 고객·주문 데이터를 NEXT_APP 스키마로 이관",
                Source = new ConnectionRef
                {
                    ProfileId = "cn-legacy-prod",
                    Schema = "LEGACY_APP"
                },
                Target = new ConnectionRef
                {
                    ProfileId = "cn-next-prod",
                    Schema = "NEXT_APP"
                },
                Strategy = new MigrationStrategy
                {
                    Mode = ExecutionModes.Full,
                    IncrementalBy = "PK",
                    CommitSize = 10000,
                    FetchSize = 5000,
                    ErrorPolicy = ErrorPolicies.Continue,
                    ErrorTable = "ERR$_",
                    Workers = 4
                },
                Checkpoints = new Dictionary<string, CheckpointInfo>(StringComparer.Ordinal)
                {
                    {
                        "tm-customer",
                        new CheckpointInfo
                        {
                            Column = "CUSTOMER_ID",
                            Value = "850000",
                            Rows = 850000,
                            Total = 1240325,
                            At = "2026-10-02 23:41:07",
                            RunId = "R-20261002-234107",
                            Status = "stopped"
                        }
                    }
                }
            };

            job.Mappings.Add(CustomerTableMapping());
            job.Mappings.Add(OrderTableMapping());
            job.Mappings.Add(GradeTableMapping());
            job.Mappings.Add(SqlMemberMapping());
            return job;
        }

        internal static MigrationJob BlankJob(MigrationJob keep)
        {
            var j = SampleJob();
            j.JobName = "NEW_MIGRATION";
            j.Description = "";
            j.Mappings = new List<Model.Mapping>();
            j.Checkpoints = new Dictionary<string, CheckpointInfo>(StringComparer.Ordinal);
            if (keep != null)
            {
                j.Source = CloneConnectionRef(keep.Source);
                j.Target = CloneConnectionRef(keep.Target);
                j.Strategy = CloneStrategy(keep.Strategy);
            }

            return j;
        }

        private static ConnectionRef CloneConnectionRef(ConnectionRef c)
        {
            if (c == null)
            {
                return new ConnectionRef();
            }

            return new ConnectionRef
            {
                ProfileId = c.ProfileId,
                Schema = c.Schema,
                Name = c.Name,
                Kind = c.Kind,
                Host = c.Host,
                Port = c.Port,
                Service = c.Service,
                User = c.User,
                Color = c.Color
            };
        }

        private static MigrationStrategy CloneStrategy(MigrationStrategy s)
        {
            if (s == null)
            {
                return new MigrationStrategy();
            }

            return new MigrationStrategy
            {
                Mode = s.Mode,
                IncrementalBy = s.IncrementalBy,
                CommitSize = s.CommitSize,
                FetchSize = s.FetchSize,
                ErrorPolicy = s.ErrorPolicy,
                ErrorTable = s.ErrorTable,
                Workers = s.Workers,
                PollIntervalSeconds = s.PollIntervalSeconds,
                MaxRunHours = s.MaxRunHours,
                LagSeconds = s.LagSeconds,
                ReconcileIntervalMinutes = s.ReconcileIntervalMinutes,
                DeleteMaxRatio = s.DeleteMaxRatio
            };
        }

        private static ColumnMapping Cm(string target, string source, string expr, string nullRule, string defaultValue)
        {
            return new ColumnMapping
            {
                Target = target,
                Source = source,
                Expr = expr ?? "",
                NullRule = nullRule,
                DefaultValue = defaultValue ?? ""
            };
        }

        private static Model.Mapping CustomerTableMapping()
        {
            return new Model.Mapping
            {
                Id = "tm-customer",
                Use = true,
                SourceType = SourceTypes.Table,
                Source = "SRC_CUSTOMER",
                Target = "TB_MEMBER",
                Mode = WriteModes.Merge,
                MergeKey = new List<string> { "MEMBER_ID" },
                CheckpointColumn = "CUSTOMER_ID",
                Where = "",
                Columns = new List<ColumnMapping>
                {
                    Cm("MEMBER_ID", "CUSTOMER_ID", "", NullRules.Reject, ""),
                    Cm("MEMBER_NAME", "CUSTOMER_NM", "TRIM(CUSTOMER_NM)", NullRules.Reject, ""),
                    Cm("MOBILE_NO", "PHONE_NO", "REGEXP_REPLACE(PHONE_NO, '[^0-9]', '')", NullRules.Allow, ""),
                    Cm("USE_YN", "STATUS_CD", "CASE\n    WHEN STATUS_CD = 'A' THEN 'Y'\n    ELSE 'N'\nEND", NullRules.Default, "Y"),
                    Cm("MEMBER_GRADE", null, "", NullRules.Allow, ""),
                    Cm("CREATED_AT", "REG_DT", "CAST(REG_DT AS TIMESTAMP)", NullRules.Sysdate, ""),
                    Cm("UPDATED_AT", "MOD_DT", "CAST(NVL(MOD_DT, REG_DT) AS TIMESTAMP)", NullRules.Allow, "")
                }
            };
        }

        private static Model.Mapping OrderTableMapping()
        {
            return new Model.Mapping
            {
                Id = "tm-order",
                Use = true,
                SourceType = SourceTypes.Table,
                Source = "SRC_ORDER",
                Target = "TB_SALES_ORDER",
                Mode = WriteModes.InsertOnly,
                MergeKey = new List<string> { "ORDER_NO" },
                CheckpointColumn = "ORDER_NO",
                Where = "ORDER_STAT_CD <> '00'",
                Columns = new List<ColumnMapping>
                {
                    Cm("ORDER_NO", "ORDER_NO", "", NullRules.Reject, ""),
                    Cm("MEMBER_ID", "CUSTOMER_ID", "", NullRules.Reject, ""),
                    Cm("ORDERED_AT", "ORDER_DT", "CAST(ORDER_DT AS TIMESTAMP)", NullRules.Reject, ""),
                    Cm("TOTAL_AMT", "ORDER_AMT", "", NullRules.Allow, ""),
                    Cm("ORDER_STATUS", "ORDER_STAT_CD", "DECODE(ORDER_STAT_CD,\n       '10', 'ORDERED',\n       '20', 'PAID',\n       '30', 'SHIPPED',\n       '90', 'CANCELED',\n       'UNKNOWN')", NullRules.Reject, ""),
                    Cm("CHANNEL_CD", null, "", NullRules.Reject, ""),
                    Cm("CREATED_AT", "REG_DT", "CAST(REG_DT AS TIMESTAMP)", NullRules.Allow, "")
                }
            };
        }

        private static Model.Mapping GradeTableMapping()
        {
            return new Model.Mapping
            {
                Id = "tm-grade",
                Use = true,
                SourceType = SourceTypes.Table,
                Source = "SRC_CUSTOMER_GRADE",
                Target = "TB_MEMBER_GRADE",
                Mode = WriteModes.TruncateInsert,
                MergeKey = new List<string> { "GRADE_CODE" },
                CheckpointColumn = null,
                Where = "",
                Columns = new List<ColumnMapping>
                {
                    Cm("GRADE_CODE", "GRADE_CD", "", NullRules.Reject, ""),
                    Cm("GRADE_NAME", "GRADE_NM", "TRIM(GRADE_NM)", NullRules.Reject, ""),
                    Cm("DISPLAY_ORDER", "SORT_SEQ", "", NullRules.Allow, "")
                }
            };
        }

        private static Model.Mapping SqlMemberMapping()
        {
            var cols = new[]
            {
                "MEMBER_ID", "MEMBER_NAME", "MOBILE_NO", "USE_YN", "MEMBER_GRADE", "CREATED_AT", "UPDATED_AT"
            };
            var columns = new List<ColumnMapping>();
            foreach (var c in cols)
            {
                var nullRule = c == "MEMBER_ID" || c == "MEMBER_NAME"
                    ? NullRules.Reject
                    : c == "USE_YN"
                        ? NullRules.Default
                        : NullRules.Allow;
                var def = c == "USE_YN" ? "Y" : "";
                columns.Add(Cm(c, c, "", nullRule, def));
            }

            return new Model.Mapping
            {
                Id = "tm-sql-member",
                Use = false,
                SourceType = SourceTypes.Sql,
                Source = "SQLMAP_MEMBER",
                Sql = SampleSql,
                Target = "TB_MEMBER",
                Mode = WriteModes.Merge,
                MergeKey = new List<string> { "MEMBER_ID" },
                CheckpointColumn = "MEMBER_ID",
                Where = "",
                FetchSize = 5000,
                CommitSize = 10000,
                Binds = new List<BindParameter>
                {
                    new BindParameter
                    {
                        Name = "LAST_ID",
                        Type = "NUMBER",
                        Value = "850000",
                        FromCheckpoint = true
                    }
                },
                Columns = columns
            };
        }
    }
}
