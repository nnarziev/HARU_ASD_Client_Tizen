using System;
using Tizen.Wearable.CircularUI.Forms;
using Xamarin.Forms.Xaml;
using System.Diagnostics;
using System.IO;
using Tizen.Sensor;
using HARU_ASD.Model;
using Tizen.Security;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using System.Json;
using Tizen.Network.WiFi;
using Tizen.System;
using Xamarin.Forms;

namespace HARU_ASD
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class MainPage : CirclePage
    {
        public MainPage()
        {
            InitializeComponent();
            InitDataSourcesWithPrivileges();
        }

        protected override void OnAppearing()
        {
            Power.RequestCpuLock(0);

            TerminateFilesCounterThread();
            StartFilesCounterThread();

            if (CountSensorDataFiles() > 1)
                Device.BeginInvokeOnMainThread(() => { EnableUploadBtn(true); });
            else
                Device.BeginInvokeOnMainThread(() => { EnableUploadBtn(false); });

            if (WiFiManager.ConnectionState == WiFiConnectionState.Connected)
                Device.BeginInvokeOnMainThread(() => { InternetStatusText(true); });
            else
                Device.BeginInvokeOnMainThread(() => { InternetStatusText(false); });

            //Check service is running and enable / disable buttons
            if (dataCollectorThread != null)
            {
                if (!dataCollectorThread.ThreadState.Equals(System.Threading.ThreadState.Running))
                {
                    Device.BeginInvokeOnMainThread(() =>
                    {
                        ServiceStatusText(false);
                        EnableStartBtn(true);
                        EnableStopBtn(false);
                        EnableSignOutBtn(true);
                    });
                }
                else
                {
                    Device.BeginInvokeOnMainThread(() =>
                    {
                        ServiceStatusText(true);
                        EnableStartBtn(false);
                        EnableStopBtn(true);
                        EnableSignOutBtn(false);
                    });
                }
            }
            else
            {
                Device.BeginInvokeOnMainThread(() =>
                {
                    ServiceStatusText(false);
                    EnableStartBtn(true);
                    EnableStopBtn(false);
                    EnableSignOutBtn(true);
                });
            }
            base.OnAppearing();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
        }

        private void InitDataSourcesWithPrivileges()
        {
            PrivacyPrivilegeManager.ResponseContext context = null;
            if (PrivacyPrivilegeManager.GetResponseContext(Tools.HEALTHINFO_PRIVILEGE).TryGetTarget(out context))
                context.ResponseFetched += (s, e) =>
                {
                    if (e.result != RequestResult.AllowForever)
                    {
                        Toast.DisplayText("Please provide the necessary privileges for the application to run!");
                        Environment.Exit(1);
                    }
                    else
                        InitDataSources();
                };
            else
            {
                Toast.DisplayText("Please provide the necessary privileges for the application to run!");
                Environment.Exit(1);
            }

            switch (PrivacyPrivilegeManager.CheckPermission(Tools.HEALTHINFO_PRIVILEGE))
            {
                case CheckResult.Allow:
                    InitDataSources();
                    break;
                case CheckResult.Deny:
                    Toast.DisplayText("Please provide the necessary privileges for the application to run!");
                    Environment.Exit(1);
                    break;
                case CheckResult.Ask:
                    PrivacyPrivilegeManager.RequestPermission(Tools.HEALTHINFO_PRIVILEGE);
                    break;
                default:
                    break;
            }
        }

        private void InitDataSources()
        {
            #region Assign sensor model references
            AccelerometerModel = new AccelerometerModel
            {
                IsSupported = Accelerometer.IsSupported,
                SensorCount = Accelerometer.Count
            };
            HRMModel = new HRMModel
            {
                IsSupported = HeartRateMonitor.IsSupported,
                SensorCount = HeartRateMonitor.Count
            };
            #endregion

            #region Assign sensor references and sensor measurement event handlers
            if (AccelerometerModel.IsSupported)
            {
                Accelerometer = new Accelerometer();
                Accelerometer.Interval = 28;
                Accelerometer.PausePolicy = SensorPausePolicy.None;
                Accelerometer.DataUpdated += storeAccelerometerDataCallback;
            }
            if (HRMModel.IsSupported)
            {
                HRM = new HeartRateMonitor();
                HRM.Interval = Tools.SENSOR_SAMPLING_INTERVAL;
                HRM.DataUpdated += storeHeartRateMonitorDataCallback;
                HRM.PausePolicy = SensorPausePolicy.None;
            }
            #endregion
        }

        public void SignOutClicked(object sender, EventArgs e)
        {
            TerminateDataCollectorThread();
            TerminateFilesCounterThread();
            TerminateSubmitDataThread();
            Tizen.Applications.Preference.Set("logged_in", false);
            Tizen.Applications.Preference.Set("username", "");
            Tizen.Applications.Preference.Set("password", "");

            EraseSensorData(); //removing all sensor data when logged out

            Device.BeginInvokeOnMainThread(() =>
            {
                IsEnabled = true;
                Navigation.PushModalAsync(new AuthenticationPage());
            });

        }

        #region Variables
        private const string TAG = "MainPage";

        private Thread filesCounterThread;
        private Thread submitDataThread;
        private Thread dataCollectorThread;

        private bool stopFilesCounterThread;
        private bool stopSubmitDataThread;
        private bool stopCollectorThread;

        private bool isDataSubmitRunning;

        private string openLogStreamStamp;
        private StreamWriter logStreamWriter;
        private int logLinesCount = 1;

        private int filesCount;

        private long prevHeartbeatTime = 0;

        // Sensors and their SensorModels
        internal AccelerometerModel AccelerometerModel { get; private set; }
        internal Accelerometer Accelerometer { get; private set; }
        internal HRMModel HRMModel { get; private set; }
        internal HeartRateMonitor HRM { get; private set; }
        #endregion

        #region UI Event callbacks
        private void ReportDataCollectionClick(object sender, EventArgs e)
        {
            if (CountSensorDataFiles() > 1 && WiFiManager.ConnectionState == WiFiConnectionState.Connected)
            {
                if (submitDataThread == null || !submitDataThread.ThreadState.Equals(System.Threading.ThreadState.Running))
                    StartSubmitDataThread();
            }
        }

        private void StartDataCollectionClick(object sender, EventArgs e)
        {
            if (dataCollectorThread == null || !dataCollectorThread.ThreadState.Equals(System.Threading.ThreadState.Running))
            {
                stopCollectorThread = false;
                StartDataCollectorThread();
            }
        }

        private void StopDataCollectionClick(object sender, EventArgs e)
        {
            TerminateDataCollectorThread();
            startDataColButton.IsEnabled = true;
            stopDataColButton.IsEnabled = false;
        }
        #endregion

        #region Sensor DataUpdated Callbacks
        private void storeAccelerometerDataCallback(object sender, AccelerometerDataUpdatedEventArgs e)
        {
            CheckUpdateCurrentLogStream();
            logStreamWriter?.Flush();
            lock (logStreamWriter)
            {
                logStreamWriter?.WriteLine($"{Tools.DATA_SRC_ACC},{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()} {e.X} {e.Y} {e.Z} 0");
            }
        }

        private void storeHeartRateMonitorDataCallback(object sender, HeartRateMonitorDataUpdatedEventArgs e)
        {
            CheckUpdateCurrentLogStream();
            logStreamWriter?.Flush();
            if (e.HeartRate > 0)
            {
                lock (logStreamWriter)
                {
                    logStreamWriter?.WriteLine($"{Tools.DATA_SRC_HRM},{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()} {e.HeartRate} 0");
                }
            }
        }
        #endregion

        private void StartSensors()
        {
            Accelerometer?.Start();
            HRM?.Start();
        }

        private void StopSensors()
        {
            Accelerometer?.Stop();
            HRM?.Stop();
        }

        private void EraseSensorData()
        {
            foreach (string file in Directory.GetFiles(Tools.APP_DIR, "*.csv"))
                File.Delete(file);
        }

        private int CountSensorDataFiles()
        {
            return Directory.GetFiles(Tools.APP_DIR, "*.csv").Length;
        }

        private void CheckUpdateCurrentLogStream()
        {
            DateTime nowTimestamp = DateTime.UtcNow.ToLocalTime();
            nowTimestamp = new DateTime(year: nowTimestamp.Year, month: nowTimestamp.Month, day: nowTimestamp.Day, hour: nowTimestamp.Hour, minute: nowTimestamp.Minute - nowTimestamp.Minute % Tools.NEW_FILE_CREATE_PERIOD, second: 0);
            string nowStamp = $"{new DateTimeOffset(nowTimestamp).ToUnixTimeMilliseconds()}";

            if (logStreamWriter == null)
            {
                openLogStreamStamp = nowStamp;
                string filePath = Path.Combine(Tools.APP_DIR, $"sw_{nowStamp}.csv");
                logStreamWriter = new StreamWriter(path: filePath, append: true);

                Log("Data-log file created/attached");
                Tools.sendHeartBeatMessage();
            }
            else if (!nowStamp.Equals(openLogStreamStamp))
            {
                logStreamWriter.Flush();
                logStreamWriter.Close();
                openLogStreamStamp = nowStamp;
                string filePath = Path.Combine(Tools.APP_DIR, $"sw_{nowStamp}.csv");
                logStreamWriter = new StreamWriter(path: filePath, append: false);

                Log("New data-log file created");
                Tools.sendHeartBeatMessage();
            }
        }

        private void TerminateDataCollectorThread()
        {
            if (dataCollectorThread != null && dataCollectorThread.IsAlive)
            {
                StopSensors();
                stopCollectorThread = true;
                dataCollectorThread?.Join();
                stopCollectorThread = false;
            }
        }

        private void StartDataCollectorThread()
        {
            dataCollectorThread = new Thread(() =>
            {
                Device.BeginInvokeOnMainThread(() =>
                {
                    ServiceStatusText(true);
                    EnableStartBtn(false);
                    EnableStopBtn(true);
                    EnableSignOutBtn(false);
                });

                while (!stopCollectorThread)
                {
                    Tizen.Log.Debug(TAG, "Files: " + CountSensorDataFiles());

                    if (!isDataSubmitRunning && CountSensorDataFiles() > 1)
                        Device.BeginInvokeOnMainThread(() => { EnableUploadBtn(true); });
                    else
                        Device.BeginInvokeOnMainThread(() => { EnableUploadBtn(false); });

                    long curTime = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();

                    if (WiFiManager.ConnectionState == WiFiConnectionState.Connected)
                    {
                        Device.BeginInvokeOnMainThread(() => { InternetStatusText(true); });
                        if (curTime > prevHeartbeatTime + Tools.HEARTBEAT_PERIOD * 1000)
                        {
                            Tools.sendHeartBeatMessage();
                            prevHeartbeatTime = curTime;
                        }
                    }
                    else
                    {
                        Device.BeginInvokeOnMainThread(() => { InternetStatusText(false); });
                    }

                    WakeUpCpu();
                    if (!Accelerometer.IsSensing || !HRM.IsSensing)
                        StartSensors();

                    Thread.Sleep(2000);
                }

                Device.BeginInvokeOnMainThread(() =>
                {
                    ServiceStatusText(false);
                    EnableStartBtn(true);
                    EnableStopBtn(false);
                    EnableSignOutBtn(true);
                });
            });
            dataCollectorThread.IsBackground = true;
            dataCollectorThread.Start();
        }

        private void TerminateFilesCounterThread()
        {
            if (filesCounterThread != null && filesCounterThread.IsAlive)
            {
                stopFilesCounterThread = true;
                filesCounterThread?.Join();
                stopFilesCounterThread = false;
            }
        }

        private void StartFilesCounterThread()
        {
            filesCounterThread = new Thread(() =>
            {
                using (FileSystemWatcher watcher = new FileSystemWatcher(Tools.APP_DIR))
                {
                    filesCount = CountSensorDataFiles();

                    Device.BeginInvokeOnMainThread(() => { filesCountLabel.Text = $"FILES: {filesCount}"; });

                    watcher.Filter = "*.csv";
                    watcher.Deleted += (s, e) => { filesCountLabel.Text = $"FILES: {--filesCount}"; };
                    watcher.Created += (s, e) =>
                    {
                        filesCountLabel.Text = $"FILES: {++filesCount}";
                        if (filesCount <= 2)
                            Device.BeginInvokeOnMainThread(() => { EnableUploadBtn(false); });
                        else
                            Device.BeginInvokeOnMainThread(() => { EnableUploadBtn(true); });
                    };

                    watcher.EnableRaisingEvents = true;
                    while (!stopFilesCounterThread) ;

                    watcher.EnableRaisingEvents = false;
                }
            });
            filesCounterThread.IsBackground = true;
            filesCounterThread.Start();
        }

        private void TerminateSubmitDataThread()
        {
            if (submitDataThread != null && submitDataThread.IsAlive)
            {
                stopSubmitDataThread = true;
                submitDataThread?.Join();
                stopSubmitDataThread = false;
            }
        }

        private void StartSubmitDataThread()
        {
            submitDataThread = new Thread(async () =>
            {
                isDataSubmitRunning = true;
                Tizen.Log.Debug(TAG, "File submit STARTED");
                Device.BeginInvokeOnMainThread(() =>
                {
                    EnableUploadBtn(false);
                    EnableSignOutBtn(false);
                });

                // Get list of files and sort in increasing order
                string[] filePaths = Directory.GetFiles(Tools.APP_DIR, "*.csv");
                List<long> fileNamesInLong = new List<long>();
                for (int n = 0; !stopSubmitDataThread && n < filePaths.Length; n++)
                {
                    string tmp = filePaths[n].Substring(filePaths[n].LastIndexOf('_') + 1);
                    fileNamesInLong.Add(long.Parse(tmp.Substring(0, tmp.LastIndexOf('.'))));
                }
                fileNamesInLong.Sort();

                // Submit files to server except the last file
                TerminateFilesCounterThread();
                for (int n = 0; !stopSubmitDataThread && n < fileNamesInLong.Count - 1; n++)
                {
                    Device.BeginInvokeOnMainThread(() => { filesCountLabel.Text = $"{(n + 1) * 100 / fileNamesInLong.Count}% UPLOADED"; });
                    string filepath = Path.Combine(Tools.APP_DIR, $"sw_{fileNamesInLong[n]}.csv");
                    await reportToApiServer(path: filepath, postTransferTask: new Task(() => { File.Delete(filepath); }));
                }
                Device.BeginInvokeOnMainThread(() => { filesCountLabel.Text = $"100% UPLOADED"; });
                Thread.Sleep(300);
                StartFilesCounterThread();

                stopSubmitDataThread = true;
                stopSubmitDataThread = false;

                isDataSubmitRunning = false;
                Device.BeginInvokeOnMainThread(() =>
                {
                    EnableUploadBtn(true);
                    EnableSignOutBtn(true);
                });
            });
            submitDataThread.IsBackground = true;
            submitDataThread.Start();
        }

        private void ServiceStatusText(bool running)
        {
            if (running)
            {
                statusLabelService.Text = "Service: Running :)";
                statusLabelService.TextColor = Color.LightGreen;
            }
            else
            {
                statusLabelService.Text = "Service: Not running :(";
                statusLabelService.TextColor = Color.Red;
            }

        }

        private void InternetStatusText(bool connected)
        {
            if (connected)
            {
                statusLabelConnection.Text = "Internet: Connected :)";
                statusLabelConnection.TextColor = Color.LightGreen;
            }
            else
            {
                statusLabelConnection.Text = "Internet: Disconnected :(";
                statusLabelConnection.TextColor = Color.Red;
            }

        }

        private void EnableStartBtn(bool enable)
        {
            if (enable)
            {
                startDataColButton.IsEnabled = true;
                startDataColButton.Source = ImageSource.FromFile("start.png");
            }
            else
            {
                startDataColButton.IsEnabled = false;
                startDataColButton.Source = ImageSource.FromFile("start_disable.png");
            }
        }

        private void EnableStopBtn(bool enable)
        {
            if (enable)
            {
                stopDataColButton.IsEnabled = true;
                stopDataColButton.Source = ImageSource.FromFile("stop.png");
            }
            else
            {
                stopDataColButton.IsEnabled = false;
                stopDataColButton.Source = ImageSource.FromFile("stop_disable.png");
            }
        }

        private void EnableUploadBtn(bool enable)
        {
            if (enable)
            {
                reportDataColButton.IsEnabled = true;
                reportDataColButton.Source = ImageSource.FromFile("upload.png");
            }
            else
            {
                reportDataColButton.IsEnabled = false;
                reportDataColButton.Source = ImageSource.FromFile("upload_disable.png");
            }
        }

        private void EnableSignOutBtn(bool enable)
        {
            if (enable)
            {
                signOutButton.IsEnabled = true;
            }
            else
            {
                signOutButton.IsEnabled = false;
            }
        }

        internal void Log(string message)
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                if (logLinesCount == logLabel.MaxLines)
                    logLabel.Text = $"{logLabel.Text.Substring(logLabel.Text.IndexOf('\n') + 1)}\n{message}";
                else
                {
                    logLabel.Text = $"{logLabel.Text}\n{message}";
                    logLinesCount++;
                }
            });
        }

        private void WakeUpCpu()
        {
            Power.RequestCpuLock(0);
            Accelerometer.PausePolicy = SensorPausePolicy.None;
            HRM.PausePolicy = SensorPausePolicy.None;
        }

        private async Task reportToApiServer(
            string message = default(string),
            string path = default(string),
            Task postTransferTask = null)
        {
            if (message != default(string))
            {
                HttpResponseMessage result = await Tools.post(Tools.API_NOTIFY, new Dictionary<string, string> {
                    { "username", Tizen.Applications.Preference.Get<string>("username") },
                    { "password", Tizen.Applications.Preference.Get<string>("password") },
                    { "message", message }
                });
                if (result.IsSuccessStatusCode)
                {
                    JsonValue resJson = JsonValue.Parse(await result.Content.ReadAsStringAsync());
                    //log($"RESULT: {resJson["result"]}");
                    Debug.WriteLine(Tools.TAG, $"Message has been submitted to the Server. length={message.Length}");
                }
                else
                    Toast.DisplayText("Failed to submit a notification to server!");
            }
            else if (path != null)
            {
                HttpResponseMessage result = await Tools.post(
                    Tools.API_SUBMIT_DATA,
                    new Dictionary<string, string>
                    {
                        {"username", Tizen.Applications.Preference.Get<string>("username") },
                        {"password", Tizen.Applications.Preference.Get<string>("password") },
                    },
                    fileContent: File.ReadAllBytes(path),
                    fileName: path.Substring(path.LastIndexOf('\\') + 1)
                );
                if (result == null)
                {
                    Toast.DisplayText("Please check your WiFi connection first!");
                    return;
                }
                if (result.IsSuccessStatusCode)
                {
                    JsonValue resJson = JsonValue.Parse(await result.Content.ReadAsStringAsync());
                    ServerResult resCode = (ServerResult)int.Parse(resJson["result"].ToString());
                    if (resCode == ServerResult.OK)
                        postTransferTask?.Start();
                    /*else
                        log($"Failed to upload {path.Substring(path.LastIndexOf(Path.PathSeparator) + 1)}");*/
                }
                /*else
                    log($"Failed to upload {path.Substring(path.LastIndexOf(Path.PathSeparator) + 1)}");*/
            }
        }
    }
}