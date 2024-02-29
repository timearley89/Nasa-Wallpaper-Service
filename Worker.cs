using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
namespace Nasa_Wallpaper_Service
{
    public class Worker : BackgroundService
    {
        public const string ApiKey = "ouj8D9mMF3LafsGjA9faiDlxHmDHThprcf1xvrBx";

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern Int32 SystemParametersInfo(UInt32 action, UInt32 uParam, String vParam, UInt32 winIni);

        private static readonly UInt32 SPI_SETDESKWALLPAPER = 0x14;
        private static readonly UInt32 SPIF_UPDATEINIFILE = 0x01;
        private static readonly UInt32 SPIF_SENDWININICHANGE = 0x02;

        private readonly ILogger<Worker> _logger;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                }
                await GetWallpaper(_logger);
                //sleep for one hour, then run again.
                _logger.LogInformation($"{DateTime.Now}: Run complete. Sleeping til next run...");
                Thread.Sleep(3600000);
            }
        }

        public async static Task GetWallpaper(ILogger<Worker> logger)
        {
            logger.LogInformation($"{DateTime.Now}: Sending Request...");
            await DownloadImageOfTheDay(logger);
            
        }

        public async static Task DownloadImageOfTheDay(ILogger<Worker> logger)
        {
            using (HttpResponseMessage myResponse = await ApiClient.NasaClient.GetAsync($"https://api.nasa.gov/planetary/apod?api_key={ApiKey}"))
            {
                if (myResponse.IsSuccessStatusCode)
                {
                    logger.LogInformation($"{DateTime.Now}: Request successful. Parsing response...");
                    try
                    {
                        IOTD? imageOTD = await myResponse.Content.ReadFromJsonAsync<IOTD>();
                        if (imageOTD != null)
                        {
                            logger.LogInformation($"{DateTime.Now}:Parsing complete. Downloading image from stream...");
                            Uri imgUri = new(imageOTD.Url);
                            JpegBitmapDecoder decoder = new JpegBitmapDecoder(imgUri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.Default);
                            BitmapFrame tempStorage = decoder.Frames.First();
                            while (decoder.IsDownloading)
                            {
                                Thread.Sleep(50);
                            }
                            DownloadCompleted(decoder, EventArgs.Empty, logger);
                            return;
                        }
                        else
                        {
                            throw new ArgumentNullException(nameof(myResponse.Content), myResponse.ReasonPhrase);
                        }
                    }
                    catch (Exception ex) { throw new Exception("Response Parse Exception", ex); }
                }
                else
                {
                    throw new HttpRequestException(myResponse.ReasonPhrase, null, myResponse.StatusCode);
                }
            }
        }

        public static void ApplyWallpaper(string imagePath, ILogger<Worker> logger)
        {
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
            //set style to span
            key!.SetValue(@"WallpaperStyle", 22.ToString());
            //do not tile
            key!.SetValue(@"TileWallpaper", 0.ToString());


            int status = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDWININICHANGE);
            logger.LogInformation($"{DateTime.Now}: Wallpaper applied!");
        }

        public static void DownloadCompleted(object? sender, EventArgs e, ILogger<Worker> logger)
        {
            if (sender == null) 
            {
                logger.LogError($"{DateTime.Now}: Decoder is null. Stream download error.");
                return; 
            }
            logger.LogInformation($"{DateTime.Now}: Download complete. Saving image to disk...");
            JpegBitmapDecoder image = (JpegBitmapDecoder)sender;
            JpegBitmapEncoder encoder = new JpegBitmapEncoder();
            string imagePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"/Earleytech/Nasa Wallpaper Service/";
            if (!Directory.Exists(imagePath))
            {
                Directory.CreateDirectory(imagePath);
            }
            FileStream fs = new FileStream(imagePath + "ImageOTD.jpeg",
                FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (image.Frames.Any())
            {
                encoder.Frames.Add(image.Frames.First());
                encoder.Save(fs);
            }
            fs.Dispose();
            logger.LogInformation($"{DateTime.Now}: Save complete. Applying wallpaper...");
            ApplyWallpaper(imagePath + "ImageOTD.jpeg", logger);
        }

        public static void DownloadFailed(object? sender, EventArgs e)
        {
            throw new FileLoadException("Image could not be downloaded");
        }

        public static void DownloadProgress(object? sender, DownloadProgressEventArgs e)
        {
            MessageBox.Show($"Progress Updated: {e.Progress}%");
            return;
        }
    }
    public class IOTD
    {
        public string Url { get; set; } = "";
    }
}
