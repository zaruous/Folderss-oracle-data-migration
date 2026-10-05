using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MigrationStudio.Core.Mapping
{
    /// <summary>POC mapping.js TABLE_DICT · COLUMN_DICT · SUFFIX_RULES.</summary>
    public static class NameDictionary
    {
        private static readonly IReadOnlyDictionary<string, string> TableDict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "CUSTOMER", "MEMBER" },
            { "CUSTOMER_GRADE", "MEMBER_GRADE" },
            { "ORDER", "SALES_ORDER" },
            { "ORDER_ITEM", "SALES_ORDER_ITEM" },
            { "CODE_MST", "COMMON_CODE" }
        };

        private static readonly IReadOnlyDictionary<string, string> ColumnDict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "CUSTOMER_ID", "MEMBER_ID" },
            { "CUSTOMER_NM", "MEMBER_NAME" },
            { "PHONE_NO", "MOBILE_NO" },
            { "STATUS_CD", "USE_YN" },
            { "REG_DT", "CREATED_AT" },
            { "MOD_DT", "UPDATED_AT" },
            { "GRADE_CD", "GRADE_CODE" },
            { "GRADE_NM", "GRADE_NAME" },
            { "SORT_SEQ", "DISPLAY_ORDER" },
            { "ORDER_DT", "ORDERED_AT" },
            { "ORDER_AMT", "TOTAL_AMT" },
            { "ORDER_STAT_CD", "ORDER_STATUS" },
            { "PRODUCT_CD", "PRODUCT_ID" },
            { "PRODUCT_NM", "PRODUCT_NAME" },
            { "CATEGORY_CD", "CATEGORY_ID" },
            { "PRICE", "LIST_PRICE" },
            { "USE_FLAG", "USE_YN" },
            { "ITEM_SEQ", "LINE_NO" },
            { "QTY", "QUANTITY" },
            { "CODE_GRP", "GROUP_CODE" },
            { "CODE_NM", "CODE_NAME" }
        };

        private static readonly IReadOnlyList<KeyValuePair<string, string>> SuffixRulesList = new[]
        {
            new KeyValuePair<string, string>("_NM", "_NAME"),
            new KeyValuePair<string, string>("_CD", "_CODE"),
            new KeyValuePair<string, string>("_DT", "_AT"),
            new KeyValuePair<string, string>("_DT", "_DATE"),
            new KeyValuePair<string, string>("_FLAG", "_YN")
        };

        private static readonly Regex TablePrefixRx = new Regex(@"^(SRC|TB|TBL|T|MST)_", RegexOptions.CultureInvariant);
        private static readonly Regex TableSuffixRx = new Regex(@"_(TB|T)$", RegexOptions.CultureInvariant);

        public static IReadOnlyDictionary<string, string> Tables
        {
            get { return TableDict; }
        }

        public static IReadOnlyDictionary<string, string> Columns
        {
            get { return ColumnDict; }
        }

        public static IReadOnlyList<KeyValuePair<string, string>> SuffixRules
        {
            get { return SuffixRulesList; }
        }

        public static string NormalizeTable(string name)
        {
            var upper = (name ?? "").ToUpperInvariant();
            upper = TablePrefixRx.Replace(upper, "");
            upper = TableSuffixRx.Replace(upper, "");
            return upper;
        }
    }
}
