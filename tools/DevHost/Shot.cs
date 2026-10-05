using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevHost
{
    internal static class Shot
    {
        private static int Save(RenderTargetBitmap bmp, string outputPath, Window window)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var fs = File.Create(outputPath))
            {
                encoder.Save(fs);
            }

            window.Close();
            return 0;
        }

        public static int Capture(Window window, string outputPath, int width, int height, bool natural = false)
        {
            try
            {
                if (natural)
                {
                    // 팝업(대화상자): 화면 크기로 늘리지 않고 창이 가진 내용 크기대로 찍는다
                    window.WindowStyle = WindowStyle.None;
                    window.ShowInTaskbar = false;
                    window.SizeToContent = SizeToContent.Height;
                    window.Show();
                    window.UpdateLayout();
                    width = (int)Math.Ceiling(window.ActualWidth);
                    height = (int)Math.Ceiling(window.ActualHeight);
                    var natBmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    natBmp.Render(window);
                    return Save(natBmp, outputPath, window);
                }

                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
                window.ShowInTaskbar = false;
                window.Width = width;
                window.Height = height;
                window.Show();
                window.UpdateLayout();
                // 콘텐츠 루트만 그리면 루트의 바깥 여백이 크기에 더해져 오른쪽이 잘리고 창 배경도 빠진다 — 창 전체를 그린다
                window.Measure(new Size(width, height));
                window.Arrange(new Rect(0, 0, width, height));
                window.UpdateLayout();
                var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(window);
                return Save(bmp, outputPath, window);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }
}
