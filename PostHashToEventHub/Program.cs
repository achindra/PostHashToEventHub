using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.EventHubs;
using System.Security.Cryptography;
using System.IO;
using System.Diagnostics;
using System.Net;
using Newtonsoft.Json;
using System.Management;
using Microsoft.ServiceBus.Messaging;
using System.Threading;
using System.Runtime.Serialization;
using System.Net.Http;
using System.Net.Http.Headers;

namespace PostHashToEventHub
{
    [DataContract(Name = "Package", Namespace = "http://www.giGABits.co.in")]
    class ModInfo
    {
        [DataMember]
        public string MachineName { get; set; }
        [DataMember]
        public string ProcessPid { get; set; }
        [DataMember]
        public string FileName { get; set; }
        [DataMember]
        public string FileSha1Hash { get; set; }
    }

    class Program
    {
        private static Microsoft.Azure.EventHubs.EventHubClient eventHubClient;
        private const string eventHubConnString = "Endpoint=sb://giscoringhub.servicebus.windows.net/;SharedAccessKeyName=SendEvents;SharedAccessKey=cVTaraswLo8d+QF7+cAn2iq29ZZfTxvy5k30veUhtFg=;EntityPath=giscoringhub_binhash";
        private const string eventHubEntityPath = "";

        static void Main(string[] args)
        {
            Thread FileUploadThread = new Thread(new ThreadStart(FileUploadWatcherAsync));
            FileUploadThread.Start();
            MonitorProcessLaunch();
            FileUploadThread.Abort();
        }

        private static async Task SendMessageToEventHub(string modInfoJson)
        {
            Console.WriteLine(modInfoJson);
            Microsoft.Azure.EventHubs.EventData eventData = new Microsoft.Azure.EventHubs.EventData(Encoding.UTF8.GetBytes(modInfoJson));
            await eventHubClient.SendAsync(eventData);
        }

        public static string Sha1HashFromFile(string fileName)
        {

            SHA1 sha1Crypto = new SHA1CryptoServiceProvider();
            FileStream file = new FileStream(fileName, FileMode.Open, FileAccess.Read);

            byte[] retHash = sha1Crypto.ComputeHash(file);
            file.Close();

            StringBuilder retStr = new StringBuilder();
            for (int i = 0; i < retHash.Length; i++)
            {
                retStr.Append(retHash[i].ToString("x2"));
            }
            return retStr.ToString();
        }

        public static void MonitorProcessLaunch()
        {
            ManagementEventWatcher meWatcher = new ManagementEventWatcher(new
                  WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            meWatcher.EventArrived += new EventArrivedEventHandler(meWatcherCallBack);
            meWatcher.Start();
            Console.ReadLine();
            meWatcher.Stop();
        }

        public static void meWatcherCallBack(object sender, EventArrivedEventArgs e)
        {
            try
            {
                int dwPid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                Console.WriteLine("Pid: {0}", dwPid);
                Process proc = Process.GetProcessById(dwPid);
                ProcessModuleCollection modules = proc.Modules;
                foreach (ProcessModule module in modules)
                {
                    ModInfo modInfo = new ModInfo();
                    modInfo.MachineName = Dns.GetHostName();
                    modInfo.ProcessPid = dwPid.ToString();
                    modInfo.FileName = module.FileName.ToString();
                    modInfo.FileSha1Hash = Sha1HashFromFile(modInfo.FileName);

                    eventHubClient = Microsoft.Azure.EventHubs.EventHubClient.CreateFromConnectionString(eventHubConnString);
                    var task = Task.Run(async () =>
                    {
                        await SendMessageToEventHub(JsonConvert.SerializeObject(modInfo));
                        await eventHubClient.CloseAsync();
                    });
                }
                Thread.Sleep(60000);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception: {0}", ex.ToString());
            }
        }

        public static void FileUploadWatcherAsync()
        {
            string connectionString = "Endpoint=sb://giscoringsb.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=KYf6djseFmB27t3ZFpfrDF8HE4+Zj6Qq+rm93cFvNGU=";

            var queueClient = QueueClient.CreateFromConnectionString(connectionString, "gifileuploadqueue");
            while (true)
            {
                try
                {

                    BrokeredMessage message = queueClient.Receive(TimeSpan.FromMinutes(1));
                    if (message != null)
                    {
                        ModInfo modInfo = message.GetBody<ModInfo>(new DataContractSerializer(typeof(ModInfo)));
                        if ((File.Exists(modInfo.FileName)) && (Sha1HashFromFile(modInfo.FileName).Equals(modInfo.FileSha1Hash)))
                        {
                            Console.WriteLine($"Cythereal File Upload Request: {modInfo.FileName}");
                            message.Complete();
                            HttpClient httpClient = new HttpClient();
                            var fileStream = File.Open(modInfo.FileName, FileMode.Open, FileAccess.Read);
                            var request = new HttpRequestMessage();
                            var content = new MultipartFormDataContent();
                            content.Add(new StreamContent(fileStream), "file", Path.GetFileName(modInfo.FileName));
                            request.Method = HttpMethod.Post;
                            request.Content = content;
                            request.RequestUri = new Uri("http://giscore.azurewebsites.net/api/blobs/upload");

                            HttpResponseMessage response = httpClient.SendAsync(request).Result;
                            Console.WriteLine(response.Content.ReadAsStringAsync().Result);
                            /*httpClient.SendAsync(request).ContinueWith(task =>
                            {
                                if(task.Result.IsSuccessStatusCode)
                                {
                                    Console.WriteLine($"Cythereal File Upload Complete: {modInfo.FileName}");
                                }
                                else
                                {
                                    Console.WriteLine($"Cythereal File Upload Failed: {modInfo.FileName}");
                                }
                            });*/
                        }
                        else
                        {
                            message.Abandon();
                        }
                    }

                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.ToString());
                }
            }
        }
    }
}
