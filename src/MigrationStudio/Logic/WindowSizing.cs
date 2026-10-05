using System;

namespace MigrationStudio.Logic
{
    /// <summary>
    /// 플러그인 창 크기 규칙. Folderss 본체는 모든 플러그인 창을 900×600으로 연다 — 5단계 화면(단계 막대 + 카드 두 열)에는 좁아
    /// 처음 열릴 때 1/4 키운 1125×750으로 맞춘다. 화면 작업 영역보다 크면 그만큼만 키우고, 사용자가 이미 크기를 바꾼 창(본체 기본값이
    /// 아닌 창)은 건드리지 않는다.
    /// </summary>
    public static class WindowSizing
    {
        public const double HostDefaultWidth = 900;
        public const double HostDefaultHeight = 600;
        public const double PreferredWidth = 1125;
        public const double PreferredHeight = 750;
        private const double ScreenMargin = 40;

        public struct WindowSize
        {
            public double Width;
            public double Height;
        }

        /// <summary>본체가 준 기본 크기 그대로인가(사용자가 바꾸지 않은 새 창).</summary>
        public static bool IsHostDefault(double width, double height)
        {
            return Math.Abs(width - HostDefaultWidth) <= 1 && Math.Abs(height - HostDefaultHeight) <= 1;
        }

        /// <summary>작업 영역 안에서 가능한 선호 크기. 작업 영역이 좁으면 기본 크기 밑으로는 줄이지 않는다(그 경우 본체 값 유지).</summary>
        public static WindowSize Preferred(double workAreaWidth, double workAreaHeight)
        {
            var w = Math.Min(PreferredWidth, workAreaWidth - ScreenMargin);
            var h = Math.Min(PreferredHeight, workAreaHeight - ScreenMargin);
            return new WindowSize
            {
                Width = Math.Max(HostDefaultWidth, w),
                Height = Math.Max(HostDefaultHeight, h)
            };
        }
    }
}
