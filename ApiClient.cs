using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;

namespace Nasa_Wallpaper_Service
{
    public static class ApiClient
    {
        public static HttpClient NasaClient { get; set; } = new();

        public static void Init()
        {
            NasaClient = new HttpClient();
            NasaClient.DefaultRequestHeaders.Accept.Clear();
            NasaClient.DefaultRequestHeaders.Accept.Add(new("application/json"));
        }
    }
}
