using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.EventLog;
using System.Text.Json.Serialization;
using System.CodeDom;
using System.Collections.ObjectModel;
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
                _logger.LogInformation($"{DateTime.Now}: Run complete. Waiting til next run...");
                await Task.Delay(TimeSpan.FromHours(1));
            }
        }

        public async static Task GetWallpaper(ILogger<Worker> logger)
        {
            logger.LogInformation($"{DateTime.Now}: Sending Request...");
            await DownloadImageOfTheDay(logger);
            
        }

        public async static Task DownloadImageOfTheDay(ILogger<Worker> logger)
        {
            const string NasaImageOfTheDayQueryUrl = $"https://api.nasa.gov/planetary/apod?api_key={ApiKey}&count=1";
            const string NasaImageLibraryQueryUrl = $"https://images-api.nasa.gov/search?media_type=image&q=galaxy";
            using (HttpResponseMessage myResponse = await ApiClient.NasaClient.GetAsync(NasaImageLibraryQueryUrl))
            {
                if (myResponse.IsSuccessStatusCode)
                {
                    logger.LogInformation($"{DateTime.Now}: Request successful. Parsing response...");
                    try
                    {
                        NasaImageObject? imageObj = await myResponse.Content.ReadFromJsonAsync<NasaImageObject>();
                        if (imageObj != null)
                        {
                            logger.LogInformation($"{DateTime.Now}:Parsing complete. Downloading image from stream...");
                            JpegBitmapDecoder decoder = await GetDecoderFromJson(GetRandomImageItem(imageObj));
                            decoder.DownloadCompleted += (sender, e) => DownloadCompleted(sender, e, logger);
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
            key!.SetValue(@"WallpaperStyle", 0.ToString());
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
                if (image.Metadata != null)
                {
                    encoder.Metadata = image.Metadata;
                }
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

        public static NasaItem GetRandomImageItem(NasaImageObject nasaobj)
        {
            Random myRand = new Random();
            if (nasaobj != null && nasaobj.Collection != null && nasaobj.Collection.Items.Length != 0)
            {
                return nasaobj.Collection.Items[myRand.Next(0, nasaobj.Collection.Items.Length)];
            }
            else
            {
                throw new ArgumentNullException(nameof(nasaobj), "The input object was invalid.");
            }
        }

        public async static Task<JpegBitmapDecoder> GetDecoderFromJson(NasaItem nasaitem)
        {
            JpegBitmapDecoder myDecoder;
            if (nasaitem.URLs == null)
            {
                using (HttpResponseMessage myResponse = await ApiClient.NasaClient.GetAsync(nasaitem.Href))
                {
                    if (myResponse.IsSuccessStatusCode)
                    {
                        nasaitem.URLs = await myResponse.Content.ReadFromJsonAsync<string[]>();
                    }
                }
            }
            //create a uri with which to build the decoder. Use the largest size image url in the item.
            Uri myImageUri;
            if (nasaitem.URLs!.Any(x => x.Contains("~large.jpg")))
            {
                myImageUri = new(nasaitem.URLs!.Where(x => x.Contains("~large.jpg")).First());
            }
            else if (nasaitem.URLs!.Any(x => x.Contains("~medium.jpg")))
            {
                myImageUri = new(nasaitem.URLs!.Where(x => x.Contains("~medium.jpg")).First());
            }
            else
            {
                myImageUri = new(nasaitem.URLs!.Where(x => x.Contains("~orig.jpg")).First());
            }
            myDecoder = new(myImageUri, BitmapCreateOptions.None, BitmapCacheOption.OnDemand);
            return myDecoder;
        }
    }
    public class IOTD
    {
        public string Url { get; set; } = "";
    }
    public class NasaImageObject
    {
        public NasaImageCollection Collection { get; set; } = new();
    }
    public class NasaImageCollection
    {
        public NasaItem[] Items { get; set; } = new NasaItem[0];
        public NasaCollectionMetadata Metadata { get; set; } = new();
    }
    public class NasaItem
    {
        /// <summary>
        /// A string containing a URL for a json collection of image URL's.
        /// </summary>
        public string Href { get; set; } = "";
        public string[]? URLs { get; set; } = null;
        public NasaItemData[] Data { get; set; } = new NasaItemData[0];
    }
    public class NasaCollectionMetadata
    {
        public int Total_hits { get; set; } = 0;
    }
    public class NasaItemData
    {
        public string Center { get; set; } = "";
        public string Title { get; set; } = "";
        public string Nasa_id { get; set; } = "";
        public string Date_created { get; set; } = "";
        public string Media_type { get; set; } = "";
        public string Description { get; set; } = "";
        public string[] Keywords { get; set; } = new string[0];
    }
    public class NasaItemDataLinks
    {
        public string Href { get; set; } = "";
        public string Rel { get; set; } = "";
        public string Render { get; set; } = "";
    }
}
