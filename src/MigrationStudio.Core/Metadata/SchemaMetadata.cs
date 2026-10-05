using System;
using System.Collections.Generic;

namespace MigrationStudio.Core.Metadata
{
    /// <summary>
    /// 스키마 하나의 메타데이터(테이블·뷰·컬럼·제약·통계). Oracle 어댑터가 ALL_* 사전 뷰에서 채우고,
    /// 플러그인은 접속·스키마별로 캐시한다. SQL 원본의 결과 열도 같은 <see cref="TableMetadata"/> 모양(가상 테이블)으로 다룬다.
    /// </summary>
    public sealed class SchemaMetadata
    {
        public string Schema { get; set; }
        public List<TableMetadata> Tables { get; set; } = new List<TableMetadata>();
        public TablespaceInfo Tablespace { get; set; }
        public DateTime LoadedAt { get; set; }
        public long ElapsedMs { get; set; }
        /// <summary>캐시 파일에서 읽었으면 true(이번 창에서 새로 읽지 않음).</summary>
        public bool Cached { get; set; }

        public TableMetadata FindTable(string name)
        {
            return Tables.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class TableMetadata
    {
        public string Name { get; set; }
        /// <summary>TABLE · VIEW · SQL(SQL 원본의 가상 테이블)</summary>
        public string Kind { get; set; } = "TABLE";
        /// <summary>통계 행 수(NUM_ROWS) 또는 SQL 원본의 추정 행 수. 모르면 null.</summary>
        public long? Rows { get; set; }
        public int AvgRowLength { get; set; }
        public string Comment { get; set; } = "";
        public string TablespaceName { get; set; }
        public List<ColumnMetadata> Columns { get; set; } = new List<ColumnMetadata>();
        public List<ForeignKeyMetadata> ForeignKeys { get; set; } = new List<ForeignKeyMetadata>();

        public bool IsVirtual
        {
            get { return string.Equals(Kind, "SQL", StringComparison.Ordinal); }
        }

        public ColumnMetadata FindColumn(string name)
        {
            return Columns.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class ColumnMetadata
    {
        public string Name { get; set; }
        /// <summary>표시용 형식: VARCHAR2(30), NUMBER(15,2), CHAR(1), DATE, TIMESTAMP …(<see cref="Types.OracleType"/>로 해석)</summary>
        public string Type { get; set; }
        public bool Nullable { get; set; } = true;
        public bool PrimaryKey { get; set; }
        /// <summary>DATA_DEFAULT 원문(예: 'Y'). 없으면 null.</summary>
        public string DefaultValue { get; set; }
        public string Comment { get; set; } = "";
        public ColumnStats Stats { get; set; } = new ColumnStats();
    }

    /// <summary>열 통계·실측(검증의 "실측 최대 길이·NULL 수"). 모르는 값은 null.</summary>
    public sealed class ColumnStats
    {
        public long? Nulls { get; set; }
        /// <summary>공백만 있는 값의 수(TRIM 뒤 NULL이 되는 값).</summary>
        public long? Blanks { get; set; }
        public int? MaxLength { get; set; }
        /// <summary>숫자만 남겼을 때 최대 길이(전화번호 정제 등).</summary>
        public int? DigitsMaxLength { get; set; }
        public long? Distinct { get; set; }
        public decimal? Max { get; set; }

        public ColumnStats Clone()
        {
            return (ColumnStats)MemberwiseClone();
        }
    }

    public sealed class ForeignKeyMetadata
    {
        public string Name { get; set; }
        public List<string> Columns { get; set; } = new List<string>();
        public string RefTable { get; set; }
    }

    public sealed class TablespaceInfo
    {
        public string Name { get; set; }
        public double FreeGb { get; set; }
        public double? QuotaLeftGb { get; set; }

        /// <summary>데이터 파일 자동 확장으로 더 커질 수 있는 양(GB). DBA_DATA_FILES를 읽지 못하면 null(모름).</summary>
        public double? AutoExtendGb { get; set; }
    }
}
