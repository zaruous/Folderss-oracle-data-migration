using System.Windows;
using Folderss.Plugins;
using MigrationStudio.Core.Adapters;

namespace MigrationStudio.Services
{
    internal static class AppServices
    {
        public static IPluginManager Manager { get; set; }
        public static string DataDirectory { get; set; }
        public static string PluginDirectory { get; set; }

        /// <summary>DevHost --fake-oracle 등: 설정되면 ConnectionService가 Oracle 대신 이 어댑터를 쓴다.</summary>
        public static IDatabaseAdapter DatabaseAdapter { get; set; }

        /// <summary>
        /// 검증 서비스 만들기. 지정하지 않으면(null) 실제 검증 엔진을 쓰는 기본 구현을 쓴다. DevHost는 Oracle 없이 보이도록 가짜를 넣는다.
        /// </summary>
        public static System.Func<StudioState, ConnectionService, IValidationService> ValidationServiceFactory { get; set; }

        /// <summary>실행 후 검증 서비스 만들기. null이면 실제 엔진을 쓰는 기본 구현.</summary>
        public static System.Func<StudioState, ConnectionService, MigrationStudio.Core.Validation.IPostValidationRunner> PostValidationFactory { get; set; }

        /// <summary>실행 서비스 만들기. null이면 실제 에이전트를 쓰는 기본 구현. DevHost는 가짜를 넣는다.</summary>
        public static System.Func<StudioState, ConnectionService, IRunService> RunServiceFactory { get; set; }

        /// <summary>DevHost 전용: 값이 있으면 비밀번호 구하기가 대화상자 없이 이 값을 돌려준다.</summary>
        public static string DevHostPassword { get; set; }

        /// <summary>DevHost --shot: ShowDialog 대신 창만 띄우고 DevHostShotWindow로 캡처한다.</summary>
        public static bool DevHostCaptureMode { get; set; }
        public static Window DevHostShotWindow { get; set; }
    }
}
