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
        NasaImageObject myImageObject = new();
        Random myRandomNum = new();

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
                await GetWallpaper(_logger, myImageObject, myRandomNum);
                _logger.LogInformation($"{DateTime.Now}: Run complete. Waiting til next run...");
                await Task.Delay(TimeSpan.FromHours(1));
            }
        }

        public async static Task GetWallpaper(ILogger<Worker> logger, NasaImageObject myObject, Random myRand)
        {
            logger.LogInformation($"{DateTime.Now}: Checking internal library...");
            if (myObject.Collection.Items.Length == 0 || myObject.Collection.Items.All(x => x.HasBeenDisplayed == true))
            {
                //if we don't have any URLs or if they've all been displayed, download new ones.
                logger.LogInformation($"{DateTime.Now}: {(myObject.Collection.Items.Length == 0 ? "Library empty. Sending request..." : "All images have been displayed. Requesting new library...")}");
                myObject = await DownloadImageOfTheDay(logger);
            }
            //pick one of the images within the dictionary to display.
            logger.LogInformation($"{DateTime.Now}: Selecting Image and building decoder...");
            JpegBitmapDecoder decoder = await GetDecoderFromJson(myObject.Collection.Items.Where(x => x.HasBeenDisplayed == false)
                .ElementAt(myRand.Next(0, myObject.Collection.Items.Count(x => x.HasBeenDisplayed == false) - 1)));
            logger.LogInformation($"{DateTime.Now}: Decoder built successfully. Calling Save Operation...");
            decoder.DownloadCompleted += (sender, e) => DownloadCompleted(sender, e, logger);
            BitmapFrame tempStorage = decoder.Frames.First();
            while (decoder.IsDownloading)
            {
                Thread.Sleep(50);
            }
            DownloadCompleted(decoder, EventArgs.Empty, logger);
        }

        public async static Task<NasaImageObject> DownloadImageOfTheDay(ILogger<Worker> logger)
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
                            logger.LogInformation($"{DateTime.Now}:Parsing complete.");
                            Dictionary<string, bool> myImages = new Dictionary<string, bool>();
                            return imageObj;
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
            if (!File.Exists(imagePath)) { throw new ArgumentNullException(nameof(imagePath), "The file could not be found."); }
            WallpaperStyle wallStyle = WallpaperStyle.Fit;
            RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true);
            //set style to span
            key!.SetValue(@"WallpaperStyle", ((int)wallStyle).ToString());
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
            string imagePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Earleytech\Nasa Wallpaper Service\";
            if (!Directory.Exists(imagePath))
            {
                Directory.CreateDirectory(imagePath);
            }
            string[] imageFiles = Directory.GetFiles(imagePath);
            bool AlreadyHaveFile = false;
            string fileName = "";
            foreach (string file in imageFiles)
            {
                FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                JpegBitmapDecoder thisdecoder = new JpegBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame tempframe = thisdecoder.Frames.First();
                while (thisdecoder.IsDownloading) { Thread.Sleep(10); }
                BitmapMetadata fileMetadata = (BitmapMetadata)thisdecoder.Frames.First().Metadata;
                BitmapMetadata imageMetadata = (BitmapMetadata)image.Frames.First().Metadata;
                if (fileMetadata != null && fileMetadata.Title == imageMetadata.Title && fileMetadata.Subject == imageMetadata.Subject && fileMetadata.DateTaken == imageMetadata.DateTaken)
                {
                    //we already have this file on disk, no need to save it.
                    AlreadyHaveFile = true;
                    fileName = file;
                }
                fs.Dispose();
            }
            if (!AlreadyHaveFile)
            {
                int imageCount = imageFiles.Length;
                fileName = imagePath + $"ImageOTD{imageCount}.jpeg";
                //should the fileName be the same as on the server? Right now it isn't, I suppose it doesn't really matter...
                FileStream fs = new FileStream(fileName,
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
                logger.LogInformation($"{DateTime.Now}: Save complete. Applying wallpaper as '{fileName}'");
            }
            else { logger.LogInformation($"{DateTime.Now}: Already have file on disk. Applying wallpaper as '{fileName}'"); }
            ApplyWallpaper(fileName, logger);
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
            else { throw new HttpRequestException("Error retrieving URLs from server");}
            //create a uri with which to build the decoder. Use the largest size image url in the item.
            Uri myImageUri;
            if (nasaitem.URLs!.Any(x => x.Contains("~orig.jpg")))
            {
                myImageUri = new(nasaitem.URLs!.Where(x => x.Contains("~orig.jpg")).First());
            }
            else if (nasaitem.URLs!.Any(x => x.Contains("~large.jpg")))
            {
                myImageUri = new(nasaitem.URLs!.Where(x => x.Contains("~large.jpg")).First());
            }
            else if (nasaitem.URLs!.Any(x => x.Contains("~medium.jpg")))
            {
                myImageUri = new(nasaitem.URLs!.Where(x => x.Contains("~medium.jpg")).First());
            }
            else if (nasaitem.URLs!.Any(x => x.Contains("~small.jpg")))
            {
                myImageUri = new(nasaitem.URLs!.Where(x => x.Contains("~small.jpg")).First());
            }
            else
            {
                try
                {
                    myImageUri = new(nasaitem.URLs!.First(x => !x.Contains("metadata.json")));
                }
                catch (ArgumentNullException)
                {
                    throw new ArgumentOutOfRangeException(nameof(nasaitem), "The collection.json object does not reference an image.");
                }
            }
            nasaitem.HasBeenDisplayed = true;
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
        public bool HasBeenDisplayed { get; set; } = false;
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
    public enum WallpaperStyle
    {
        CenterOrTile = 0,
        Stretch = 2,
        Fit = 6,
        Fill = 10,
        Span = 22
    }
}
