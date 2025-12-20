using System;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

namespace SocietyManagerUpdater
{
    class Program
    {
        // 🔑 CONFIGURE THESE BEFORE BUILDING 🔑
        static readonly string UpdateBaseFileUrl = "https://YOUR_RENDER_APP.onrender.com/files/";
        static readonly string CentralLogUrl = "https://YOUR_RENDER_APP.onrender.com/api/log";

        static readonly string TargetBaseFolder = @"D:\SocietyManager";
        static readonly string TargetDebugFolder = @"D:\SocietyManager\Debug";

        // Paths
        static readonly string TempZipPath = Path.Combine(Path.GetTempPath(), "society_update.zip");
        static readonly string TempUpdaterPath = Path.Combine(Path.GetTempPath(), "new_updater.exe");
        static readonly string LogPath = Path.Combine(Path.GetTempPath(), "society_updater.log");
        static readonly string StartupKeyName = "SocietyManager_DebugUpdater";
        static readonly string FirstRunFlag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "society_mgr_firstrun.flag");
        static readonly string LastForceCheckFile = Path.Combine(Path.GetTempPath(), "society_mgr_lastforce.flag");

        static string CurrentExePath => Process.GetCurrentProcess().MainModule.FileName;

        // 🔐 Enable TLS 1.2 for modern HTTPS
        static Program()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)768 | (SecurityProtocolType)3072;
        }

        static void Main(string[] args)
        {
            bool isSilent = args.Length > 0 && args[0] == "--silent";
            if (!isSilent) Console.WriteLine("Society Manager Updater starting...");

            Log("=== Updater Started ===");
            Thread.Sleep(300000); // Wait 5 minutes for system + internet

            WaitForInternet();
            AddToStartup();
            TrySelfUpdate();

            while (true)
            {
                try
                {
                    var info = GetSocietyInfoFromLocalDb();
                    if (string.IsNullOrEmpty(info.SocietyCode))
                    {
                        Log("Could not get SocietyCode from local DB. Using default.");
                        info = (SocietyCode: "DEFAULT", SocietyEnglishName: "Default Society", UnionName: "Porbandar");
                    }

                    string union = info.UnionName ?? "Porbandar";
                    string versionFile = $"DebugSocietyManager_{union}_{DateTime.Today:ddMMyyyy}.zip";
                    string fullUrl = UpdateBaseFileUrl + versionFile;

                    bool firstRun = !File.Exists(FirstRunFlag);
                    bool isScheduled = (DateTime.Today.Day == 2);
                    bool isForce = IsForceUpdateAvailable(info.SocietyCode, union);

                    if (firstRun || isForce || isScheduled)
                    {
                        bool fileExists = true;
                        if (!firstRun && !isForce)
                        {
                            fileExists = IsUrlAccessible(fullUrl);
                        }

                        if (fileExists)
                        {
                            LogUpdateToLocalHistory("Started", versionFile);
                            try
                            {
                                DownloadFileWithResume(fullUrl, TempZipPath);
                                ExtractWithBackup(union);
                                LogUpdateToLocalHistory("Success", versionFile);
                                SendLogToServer(info.SocietyCode, info.SocietyEnglishName, union, "Success", versionFile);
                                
                                if (firstRun) File.WriteAllText(FirstRunFlag, "done");
                                if (isForce) File.WriteAllText(LastForceCheckFile, DateTime.Today.ToString("yyyy-MM-dd"));
                            }
                            catch (Exception ex)
                            {
                                string errorMsg = ex.Message;
                                LogUpdateToLocalHistory("Failed", versionFile, errorMsg);
                                SendLogToServer(info.SocietyCode, info.SocietyEnglishName, union, "Failed", versionFile, errorMsg);
                            }
                        }
                        else
                        {
                            Log("No update available on server.");
                            LogUpdateToLocalHistory("Skipped", versionFile, "File not found");
                            SendLogToServer(info.SocietyCode, info.SocietyEnglishName, union, "Skipped", versionFile, "File not found");
                        }
                    }

                    SyncHistoryToServer();
                    Thread.Sleep(1800000); // Wait 30 minutes
                }
                catch (Exception ex)
                {
                    Log("Main loop error: " + ex.Message);
                    Thread.Sleep(300000); // Wait 5 minutes
                }
            }
        }

        // =============== FORCE UPDATE CHECK ===============
        static bool IsForceUpdateAvailable(string societyCode, string unionName)
        {
            string[] flagUrls = {
                UpdateBaseFileUrl + $"society_{societyCode}_force.flag",
                UpdateBaseFileUrl + $"union_{unionName}_force.flag",
                UpdateBaseFileUrl + "global_force.flag"
            };

            foreach (string url in flagUrls)
            {
                if (IsUrlAccessible(url))
                {
                    // Avoid repeat on same day
                    if (File.Exists(LastForceCheckFile))
                    {
                        string last = File.ReadAllText(LastForceCheckFile).Trim();
                        if (last == DateTime.Today.ToString("yyyy-MM-dd"))
                            return false;
                    }
                    Log($"Force trigger detected: {url}");
                    return true;
                }
            }
            return false;
        }

        // =============== DOWNLOAD WITH RESUME & PROGRESS ===============
        static void DownloadFileWithResume(string url, string outputPath)
        {
            long downloaded = 0;
            if (File.Exists(outputPath))
                downloaded = new FileInfo(outputPath).Length;

            long totalBytes = -1;
            HttpWebResponse response = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                if (downloaded > 0)
                    request.AddRange((long)downloaded);

                request.Timeout = 60000;
                request.ReadWriteTimeout = 60000;
                response = (HttpWebResponse)request.GetResponse();
                totalBytes = response.ContentLength;
                if (downloaded > 0 && totalBytes > 0) totalBytes += downloaded;

                using (var remoteStream = response.GetResponseStream())
                using (var fileStream = new FileStream(outputPath, downloaded > 0 ? FileMode.Append : FileMode.Create))
                {
                    byte[] buffer = new byte[4096];
                    long totalRead = downloaded;
                    int lastLoggedPercent = -1;

                    int bytesRead;
                    while ((bytesRead = remoteStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        fileStream.Write(buffer, 0, bytesRead);
                        fileStream.Flush();
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            int percent = (int)((totalRead * 100) / totalBytes);
                            if (percent != lastLoggedPercent && (percent % 5 == 0 || percent == 100 || percent == 1))
                            {
                                Log($"Download: {percent}% ({totalRead / 1e6:F1} MB / {totalBytes / 1e6:F1} MB)");
                                lastLoggedPercent = percent;
                            }
                        }
                    }
                }
            }
            finally
            {
                if (response != null) response.Close();
            }
        }

        // =============== EXTRACT WITH BACKUP ===============
        static void ExtractWithBackup(string union)
        {
            if (Directory.Exists(TargetDebugFolder))
            {
                string backup = TargetDebugFolder + "_" + DateTime.Now.ToString("ddMMyyyy_HHmmss");
                Directory.Move(TargetDebugFolder, backup);
                Log($"Backed up old folder to: {backup}");
            }

            Directory.CreateDirectory(TargetBaseFolder);
            ZipFile.ExtractToDirectory(TempZipPath, TargetDebugFolder);
            File.Delete(TempZipPath);

            // Launch main application
            string mainExe = Path.Combine(TargetDebugFolder, "DebugSocietyManager.exe");
            if (File.Exists(mainExe))
            {
                Process.Start(mainExe);
            }
        }

        // =============== LOCAL HISTORY LOGGING ===============
        static void LogUpdateToLocalHistory(string status, string versionFile, string error = "")
        {
            try
            {
                EnsureLocalHistoryTableExists();
                var info = GetSocietyInfoFromLocalDb();

                string connStr = @"Server=(local);Database=SocietyManagerData;Integrated Security=true;";
                string query = @"
                    INSERT INTO DebugUpdateHistory 
                    (SocietyCode, SocietyEnglishName, UnionName, UpdateDate, UpdateTime, VersionFileName, Status, ErrorMessage)
                    VALUES (@Code, @Name, @Union, @Date, @Time, @File, @Status, @Error)";

                using (var conn = new SqlConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@Code", info.SocietyCode ?? "");
                        cmd.Parameters.AddWithValue("@Name", info.SocietyEnglishName ?? "");
                        cmd.Parameters.AddWithValue("@Union", info.UnionName ?? "");
                        cmd.Parameters.AddWithValue("@Date", DateTime.Today);
                        cmd.Parameters.AddWithValue("@Time", DateTime.Now.TimeOfDay);
                        cmd.Parameters.AddWithValue("@File", versionFile ?? "");
                        cmd.Parameters.AddWithValue("@Status", status);
                        cmd.Parameters.AddWithValue("@Error", error ?? "");
                        cmd.ExecuteNonQuery();
                    }
                }
                Log("Local history recorded.");
            }
            catch (Exception ex) { Log("Local history error: " + ex.Message); }
        }

        static void EnsureLocalHistoryTableExists()
        {
            ExecuteLocalSql(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DebugUpdateHistory')
                CREATE TABLE DebugUpdateHistory (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    SocietyCode NVARCHAR(50),
                    SocietyEnglishName NVARCHAR(255),
                    UnionName NVARCHAR(100),
                    UpdateDate DATE,
                    UpdateTime TIME,
                    VersionFileName NVARCHAR(255),
                    Status NVARCHAR(50),
                    ErrorMessage NVARCHAR(MAX),
                    CreatedOn DATETIME2 DEFAULT GETDATE(),
                    SyncedToServer BIT NOT NULL DEFAULT 0
                )");
        }

        static void ExecuteLocalSql(string query)
        {
            try
            {
                using (var conn = new SqlConnection(@"Server=(local);Database=SocietyManagerData;Integrated Security=true;"))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand(query, conn))
                        cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        // =============== SYNC TO CENTRAL SERVER ===============
        static void SyncHistoryToServer()
        {
            try
            {
                EnsureLocalHistoryTableExists();
                string selectQuery = "SELECT Id, SocietyCode, SocietyEnglishName, UnionName, UpdateDate, UpdateTime, VersionFileName, Status, ErrorMessage, CreatedOn FROM DebugUpdateHistory WHERE SyncedToServer = 0";

                using (var localConn = new SqlConnection(@"Server=(local);Database=SocietyManagerData;Integrated Security=true;"))
                {
                    localConn.Open();
                    using (var cmd = new SqlCommand(selectQuery, localConn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            SendRecordToServer(reader);
                            MarkRecordAsSynced(reader["Id"].ToString());
                        }
                    }
                }
                Log("History synced to central server.");
            }
            catch (Exception ex) { Log("Sync error: " + ex.Message); }
        }

        static void SendRecordToServer(SqlDataReader reader)
        {
            try
            {
                string clientId = Environment.MachineName + "_" + reader["SocietyCode"].ToString();
                var data = new
                {
                    clientId = clientId,
                    societyCode = reader["SocietyCode"]?.ToString() ?? "",
                    societyName = reader["SocietyEnglishName"]?.ToString() ?? "",
                    unionName = reader["UnionName"]?.ToString() ?? "",
                    status = reader["Status"]?.ToString() ?? "",
                    versionFileName = reader["VersionFileName"]?.ToString() ?? "",
                    errorMessage = reader["ErrorMessage"]?.ToString() ?? ""
                };

                string json = SerializeToJson(data);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    client.UploadData(CentralLogUrl, "POST", bytes);
                }
            }
            catch { /* Ignore sync errors */ }
        }

        static void MarkRecordAsSynced(string id)
        {
            string update = "UPDATE DebugUpdateHistory SET SyncedToServer = 1 WHERE Id = @Id";
            using (var conn = new SqlConnection(@"Server=(local);Database=SocietyManagerData;Integrated Security=true;"))
            {
                conn.Open();
                using (var cmd = new SqlCommand(update, conn))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // =============== SEND LOG TO SERVER (AFTER UPDATE) ===============
        static void SendLogToServer(string societyCode, string societyName, string unionName, string status, string versionFile, string errorMsg = "")
        {
            try
            {
                var data = new
                {
                    clientId = Environment.MachineName + "_" + societyCode,
                    societyCode = societyCode ?? "",
                    societyName = societyName ?? "",
                    unionName = unionName ?? "",
                    status = status,
                    versionFileName = versionFile,
                    errorMessage = errorMsg
                };

                string json = SerializeToJson(data);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    client.UploadData(CentralLogUrl, "POST", bytes);
                }
            }
            catch { /* Ignore */ }
        }

        // =============== MANUAL JSON SERIALIZER (.NET 4.0 COMPATIBLE) ===============
        static string SerializeToJson(object obj)
        {
            var type = obj.GetType();
            var props = type.GetProperties();
            var parts = new System.Collections.Generic.List<string>();

            foreach (var prop in props)
            {
                var value = prop.GetValue(obj, null);
                string valStr = value == null ? "null" : "\"" + value.ToString().Replace("\"", "\\\"") + "\"";
                parts.Add("\"" + prop.Name + "\":" + valStr);
            }

            return "{" + string.Join(",", parts) + "}";
        }

        // =============== SOCIETY INFO FROM LOCAL SQL ===============
        static (string SocietyCode, string SocietyEnglishName, string UnionName) GetSocietyInfoFromLocalDb()
        {
            try
            {
                string query = "SELECT TOP 1 SocietyCode, SocietyEnglishName, UnionName FROM oCompanyMaster";
                using (var conn = new SqlConnection(@"Server=(local);Database=SocietyManagerData;Integrated Security=true;"))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand(query, conn))
                    {
                        var reader = cmd.ExecuteReader();
                        if (reader.Read())
                        {
                            return (
                                reader["SocietyCode"]?.ToString(),
                                reader["SocietyEnglishName"]?.ToString(),
                                reader["UnionName"]?.ToString()
                            );
                        }
                    }
                }
            }
            catch (Exception ex) { Log("SQL Error: " + ex.Message); }
            return (null, null, null);
        }

        // =============== SELF UPDATE ===============
        static void TrySelfUpdate()
        {
            string updaterUrl = UpdateBaseFileUrl + "SocietyManagerDebugUpdater.exe";
            if (!IsUrlAccessible(updaterUrl)) return;

            Log("New updater version available. Updating self...");
            DownloadFileWithResume(updaterUrl, TempUpdaterPath);
            File.Move(CurrentExePath, CurrentExePath + ".old");
            File.Move(TempUpdaterPath, CurrentExePath);
            Process.Start(CurrentExePath, "--silent");
            Environment.Exit(0);
        }

        // =============== UTILITIES ===============
        static void WaitForInternet()
        {
            Log("Waiting for internet connection...");
            int retries = 0;
            while (retries < 90) // Max 30 minutes
            {
                if (IsInternetAvailable()) { Log("Internet connected."); return; }
                Thread.Sleep(20000); // Wait 20 seconds
                retries++;
            }
        }

        static bool IsInternetAvailable()
        {
            try
            {
                using (var client = new WebClient())
                using (client.OpenRead("http://www.google.com/robots.txt"))
                    return true;
            }
            catch { return false; }
        }

        static bool IsUrlAccessible(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch { return false; }
        }

        static void AddToStartup()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    key.SetValue(StartupKeyName, $"\"{CurrentExePath}\" --silent");
                }
            }
            catch { }
        }

        static void Log(string msg)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {msg}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
