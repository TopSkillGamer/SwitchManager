﻿using SwitchManager.nx.library;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.ObjectModel;
using System.Net.Http.Headers;
using System.Diagnostics;
using SwitchManager.util;
using System.Security.Cryptography;

namespace SwitchManager.nx.cdn
{
    public class CDNDownloader
    {
        // THIS IS ALL CONFIG
        // TO BE GOTTEN FROM A FILE, PROBABLY
        private string environment;
        private string firmware;

        private string deviceId;
        private string region;

        private string imagesPath;
        private string hactoolPath;
        private string keysPath;
        private string clientCertPath;
        private string eShopCertPath;
        private string titleCertPath;
        private string titleTicketPath;

        public int DownloadBuffer { get; set; }

        public X509Certificate ClientCert { get; private set; }
        public X509Certificate EshopCert { get; private set; }

        public List<Task> DownloadTasks { get; } = new List<Task>();

        public CDNDownloader(string clientCertPath, string eShopCertificate, string titleCertPath, string titleTicketPath, string deviceId, string firmware, string environment, string region, string imagesPath, string hactoolPath, string keysPath)
        {
            this.clientCertPath = clientCertPath;
            this.eShopCertPath = eShopCertificate;
            this.titleCertPath = titleCertPath;
            this.titleTicketPath = titleTicketPath;
            this.ClientCert = LoadSSL(clientCertPath);
            this.EshopCert = LoadSSL(eShopCertPath);
            this.deviceId = deviceId;
            this.firmware = firmware;
            this.environment = environment;
            this.region = region;
            this.imagesPath = Path.GetFullPath(imagesPath);
            this.hactoolPath = Path.GetFullPath(hactoolPath);
            this.keysPath = Path.GetFullPath(keysPath);
        }

        private X509Certificate LoadSSL(string path, string password = null)
        {
            //string contents = File.ReadAllText(path); 
            //byte[] bytes = GetBytesFromPEM(contents, "CERTIFICATE");
            //byte[] bytes = GetBytesFromPEM(contents, "RSA PRIVATE KEY");
            //var certificate = new X509Certificate2(bytes);
            var certificate = password == null ? new X509Certificate2(path) : new X509Certificate2(path, password);
            //var certificate = X509Certificate.CreateFromSignedFile(path);
            //var certificate = X509Certificate.CreateFromCertFile(path);
            return certificate;
        }

        byte[] GetBytesFromPEM(string pemString, string section)
        {
            var header = String.Format("-----BEGIN {0}-----", section);
            var footer = String.Format("-----END {0}-----", section);

            var start = pemString.IndexOf(header, StringComparison.Ordinal);
            if (start < 0)
                return null;

            start += header.Length;
            var end = pemString.IndexOf(footer, start, StringComparison.Ordinal) - start;

            if (end < 0)
                return null;

            return Convert.FromBase64String(pemString.Substring(start, end));
        }

        /// <summary>
        /// Loads a remote image from nintendo.
        /// 
        /// This is way more complicated and I know I'm gonna need more arguments passed in.
        /// Not implemented for now.
        /// </summary>
        /// <param name="titleID"></param>
        /// <returns></returns>
        public async Task DownloadRemoteImage(SwitchTitle title)
        {
            // Sanity check if no versions or null then base version of 0
            uint version;
            if ((title?.Versions?.Count ?? 0) == 0)
                version = 0;
            else
                version = title.Versions.Last(); // I don't know if this is supposed to be the newest or oldest version

            // Temporary download folder within the images folder for this title
            // Make sure directory is created first
            string gamePath = this.imagesPath + Path.DirectorySeparatorChar + title.TitleID;
            DirectoryInfo gameDir = Directory.CreateDirectory(gamePath);


            var cnmt = await DownloadAndDecryptCnmt(title, version, gamePath).ConfigureAwait(false);
            if (cnmt != null)
            {
                // Parse "control" type content entries inside the NCA (just one...)
                // Download each file (just one)

                string ncaID = cnmt.ParseNCAs(NCAType.Control).First(); // There's only one control.nca
                string fpath = gamePath + Path.DirectorySeparatorChar + "control.nca";
                if (await DownloadNCA(ncaID, fpath).ConfigureAwait(false))
                {
                    var controlDir = DecryptNCA(fpath);

                    DirectoryInfo imageDir = controlDir.EnumerateDirectories("romfs").First();

                    var iconFile = imageDir.EnumerateFiles("icon_*.dat").First(); // Get all icon files in section0, should just be one
                    iconFile.MoveTo(imagesPath + Path.DirectorySeparatorChar + title.TitleID + ".jpg");
                    gameDir.Delete(true);
                }
            }
            else
            {
                throw new Exception("No cnmtid found for title " + title.Name);
            }
        }

        /// <summary>
        /// Downloads a title + version from the CDN and repacks it if desired. Verification of downloaded files optional.
        /// </summary>
        /// <param name="title">Title to download (must include titleid and titlekey)</param>
        /// <param name="version">Title version (only applicable to updates, must be a multiple of 0x10000)</param>
        /// <param name="nspRepack">true to pack all downloaded title files into an NSP for later installation</param>
        /// <param name="verify">true to verify the SHA256 of each file with the expected hash and fail if the hashes don't match</param>
        /// <param name="titleDir">Directory to download everything to.</param>
        /// <returns></returns>
        public async Task<NSP> DownloadTitle(SwitchTitle title, uint version, string titleDir, bool nspRepack = false, bool verify = false)
        {
            Console.WriteLine($"Downloading title {title.Name}, ID: {title.TitleID}, VERSION: {version}");

            var cnmt = await DownloadAndDecryptCnmt(title, version, titleDir).ConfigureAwait(false);

            if (cnmt != null)
            {
                // Now that the CNMT NCA was downloaded and decrypted, read it f
                string ticketPath = null, certPath = null, cnmtXml = null;
                if (nspRepack)
                {
                    cnmtXml = titleDir + Path.DirectorySeparatorChar + Path.GetFileNameWithoutExtension(cnmt.CnmtNcaFilePath) + ".xml";
                    cnmt.GenerateXml(cnmtXml);

                    string rightsID = $"{title.TitleID}{new String('0', 15)}{cnmt.MasterKeyRevision}";
                    ticketPath = titleDir + Path.DirectorySeparatorChar + rightsID + ".tik";
                    certPath = titleDir + Path.DirectorySeparatorChar + rightsID + ".cert";
                    if (cnmt.Type == TitleType.Application || cnmt.Type == TitleType.AddOnContent)
                    {
                        File.Copy(this.titleCertPath, certPath, true);
                        Console.WriteLine($"Generated certificate {certPath}.");

                        if (!string.IsNullOrWhiteSpace(title.TitleKey))
                        {
                            byte[] data = File.ReadAllBytes(this.titleTicketPath);

                            // The ticket file starts with the bytes 4 0 1 0, reversed for endianness that gives
                            // 0x00010004, which indicates a RSA_2048 SHA256 signature method.
                            // The signature requires 4 bytes for the type, 0x100 for the signature and 0x3C for padding
                            // The total signature is 0x140. That explains the 0x140 mystery bytes at the start.

                            // Copy the 16-byte value of the 32 character hex title key into memory starting at position 0x180
                            for (int n = 0; n < 0x10; n++)
                            {
                                string byteValue = title.TitleKey.Substring(n * 2, 2);
                                data[0x180 + n] = byte.Parse(byteValue, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                            }

                            data[0x286] = cnmt.MasterKeyRevision;
                            // switchbrew says this should be at 0x285, not 0x286...
                            // Who's right? Does it even matter?

                            // Copy the rights ID in there too at 0x2A0, also 16 bytes (32 characters) long
                            for (int n = 0; n < 0x10; n++)
                            {
                                string byteValue = rightsID.Substring(n * 2, 2);
                                data[0x2A0 + n] = byte.Parse(byteValue, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                            }
                            Miscellaneous.HexToBytes(rightsID?.Substring(0, 32), data, 0x2A0);
                            File.WriteAllBytes(ticketPath, data);

                            Console.WriteLine($"Generated ticket {ticketPath}.");
                        }
                    }
                    else if (cnmt.Type == TitleType.Patch)
                    {
                        // We have to download the CETK file and get the ticket and the certificate from it

                        string cetkPath = $"{titleDir}{Path.DirectorySeparatorChar}{rightsID}.cetk";
                        bool completed = await DownloadCETK(rightsID, cetkPath);
                        if (completed)
                        {
                            using (var cetkStream = File.OpenRead(cetkPath))
                            {
                                cetkStream.Seek(0x180, SeekOrigin.Begin);
                                byte[] tkeyBytes = new byte[0x10];
                                cetkStream.Read(tkeyBytes, 0, 0x10);
                                title.TitleKey = Miscellaneous.BytesToHex(tkeyBytes);

                                using (var tikStream = File.OpenWrite(ticketPath))
                                {
                                    cetkStream.Seek(0, SeekOrigin.Begin);
                                    byte[] tikBytes = new byte[0x2C0];
                                    cetkStream.Read(tikBytes, 0, 0x2C0);
                                    tikStream.Write(tikBytes, 0, 0x2C0);
                                }

                                using (var certStream = File.OpenWrite(certPath))
                                {
                                    cetkStream.Seek(0x2C0, SeekOrigin.Begin);
                                    byte[] certBytes = new byte[0x700];
                                    cetkStream.Read(certBytes, 0, 0x700);
                                    certStream.Write(certBytes, 0, 0x700);
                                }
                            }
                        }
                    }
                }

                List<Task<bool>> tasks = new List<Task<bool>>();
                NSP nsp = new NSP(title, certPath, ticketPath, cnmt.CnmtNcaFilePath, cnmtXml);
                foreach (var type in new[] { NCAType.Meta, NCAType.Control, NCAType.HtmlDocument, NCAType.LegalInformation, NCAType.Program, NCAType.Data, NCAType.DeltaFragment })
                {
                    // To verify, we need to parse the CNMT more thoroughly, which is a waste of effort if we aren't verifying
                    if (verify)
                    {
                        var parsedNCAs = cnmt.ParseContent(type);
                        foreach (var content in parsedNCAs)
                        {
                            string ncaID = content.Key;
                            byte[] hash = content.Value.HashData;
                            string path = titleDir + Path.DirectorySeparatorChar + ncaID + ".nca";
                            nsp.AddNCA(type, path);
                            Task<bool> t = DoDownloadNCA(ncaID, path, hash);
                            tasks.Add(t);
                        }
                    }
                    else
                    {
                        var parsedNCAFiles = cnmt.ParseNCAs(type);
                        foreach (var ncaID in parsedNCAFiles)
                        {
                            string path = titleDir + Path.DirectorySeparatorChar + ncaID + ".nca";
                            nsp.AddNCA(type, path);
                            Task<bool> t = DoDownloadNCA(ncaID, path, null);
                            tasks.Add(t);
                        }
                    }
                }

                bool[] results = await Task.WhenAll(tasks);
                foreach (var r in results)
                {
                    if (verify && !r)
                    {
                        throw new Exception("At least one NCA failed to verify, NSP repack (if requested) will not continue");
                    }
                    else if (!r)
                    {
                        // Chances are all this means is that it was cancelled
                        // Unfortunately I did cancelling via returning false instead of true,
                        // when really I should have thrown a cancelled exception
                        // Perhaps I will update it some day.
                        Console.WriteLine("Download didn't complete. It may have been cancelled. NSPs will not be repacked, and you should try the download again later");
                        return null;
                    }
                }

                if (nspRepack)
                {
                    return nsp;
                }
            }

            return null;
        }

        private async Task<bool> DoDownloadNCA(string ncaID, string path, byte[] expectedHash)
        {
            Console.WriteLine($"Downloading NCA {ncaID}.");
            bool completed = await DownloadNCA(ncaID, path).ConfigureAwait(false);
            if (!completed) return false;

            // A null hash means no verification necessary, just return true
            if (expectedHash != null)
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] hash = new SHA256Managed().ComputeHash(fs);
                    if (expectedHash.Length != hash.Length) // hash has to be 32 bytes = 256 bit
                    {
                        Console.WriteLine($"Bad parsed hash file for {ncaID}, not the right length");
                        return false;
                    }
                    for (int i = 0; i < hash.Length; i++)
                    {
                        if (hash[i] != expectedHash[i])
                        {
                            Console.WriteLine($"Hash of downloaded NCA file does not match expected hash from CNMT content entry!");
                            return false;
                        }
                    }
                    return true;
                }
            }
            return true;
        }

        private async Task<bool> DownloadNCA(string ncaID, string path)
        {
            string url = $"https://atum.hac.{environment}.d4c.nintendo.net/c/c/{ncaID}?device_id={deviceId}";

            return await DownloadFile(url, path).ConfigureAwait(false); // download file and wait for it since we can't do anything until it is done
        }

        /// <summary>
        /// 
        /// TODO Implement DecryptNCA
        /// </summary>
        /// <param name="fpath"></param>
        /// <returns></returns>
        private DirectoryInfo DecryptNCA(string ncaPath, string outDir = null)
        {
            string fName = Path.GetFileNameWithoutExtension(ncaPath); // fName = os.path.basename(fPath).split()[0]
            if (outDir == null)
                outDir = Path.GetDirectoryName(ncaPath) + Path.DirectorySeparatorChar + Path.GetFileNameWithoutExtension(ncaPath);
            DirectoryInfo outDirInfo = new DirectoryInfo(outDir);
            outDirInfo.Create();

            string hactoolExe = (this.hactoolPath);
            string keysFile = (this.keysPath);
            string exefsPath = (outDir + Path.DirectorySeparatorChar + "exefs");
            string romfsPath = (outDir + Path.DirectorySeparatorChar + "romfs");
            string section0Path = (outDir + Path.DirectorySeparatorChar + "section0");
            string section1Path = (outDir + Path.DirectorySeparatorChar + "section1");
            string section2Path = (outDir + Path.DirectorySeparatorChar + "section2");
            string section3Path = (outDir + Path.DirectorySeparatorChar + "section3");
            string headerPath = (outDir + Path.DirectorySeparatorChar + "Header.bin");

            // NOTE: Using single quotes here instead of single quotes fucks up windows, it CANNOT handle single quotes
            // Anything surrounded in single quotes will throw an error because the file/folder isn't found
            // Must use escaped double quotes!
            string commandLine = $" -k \"{keysFile}\"" +
                                 $" --exefsdir=\"{exefsPath}\"" +
                                 $" --romfsdir=\"{romfsPath}\"" +
                                 $" --section0dir=\"{section0Path}\"" +
                                 $" --section1dir=\"{section1Path}\"" +
                                 $" --section2dir=\"{section2Path}\"" +
                                 $" --section3dir=\"{section3Path}\"" +
                                 $" --header=\"{headerPath}\"" +
                                 $" \"{ncaPath}\"";
            try
            {
                ProcessStartInfo hactoolSI = new ProcessStartInfo()
                {
                    FileName = hactoolExe,
                    WorkingDirectory = System.IO.Directory.GetCurrentDirectory(),
                    Arguments = commandLine,
                    UseShellExecute = false,
                    //RedirectStandardOutput = true,
                    //RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                Process hactool = Process.Start(hactoolSI);

                //string errors = hactool.StandardError.ReadToEnd();
                //string output = hactool.StandardOutput.ReadToEnd();

                hactool.WaitForExit();

                if (outDirInfo.GetDirectories().Length == 0)
                    throw new Exception($"Running hactool failed, output directory {outDir} is empty!");
            }
            catch (Exception e)
            {
                throw new Exception("Hactool decryption failed!", e);
            }

            return outDirInfo;
        }

        private bool VerifyNCA(string ncaPath, SwitchTitle title)
        {
            string hactoolExe = (this.hactoolPath);
            string keysFile = (this.keysPath);
            string tkey = title.TitleKey;

            // NOTE: Using single quotes here instead of single quotes fucks up windows, it CANNOT handle single quotes
            // Anything surrounded in single quotes will throw an error because the file/folder isn't found
            // Must use escaped double quotes!
            string commandLine = $" -k \"{keysFile}\"" +
                                 $" --titlekey=\"{tkey}\"" +
                                 $" \"{ncaPath}\"";
            try
            {
                ProcessStartInfo hactoolSI = new ProcessStartInfo()
                {
                    FileName = hactoolExe,
                    WorkingDirectory = System.IO.Directory.GetCurrentDirectory(),
                    Arguments = commandLine,
                    UseShellExecute = false,
                    //RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                Process hactool = Process.Start(hactoolSI);

                string errors = hactool.StandardError.ReadToEnd();
                hactool.WaitForExit();

                if (errors.Contains("Error: section 0 is corrupted!") ||
                    errors.Contains("Error: section 1 is corrupted!"))
                {
                    Console.WriteLine("NCA title key verification failed");
                    return false;
                }
            }
            catch (Exception e)
            {
                throw new Exception("Hactool decryption failed!", e);
            }

            Console.WriteLine("NCA title key verification successful");
            return true;
        }

        public async Task<long> GetContentLength(string url)
        {
            var result = await MakeRequest(HttpMethod.Get, url, null, null, false);
            long cLength = result.Content.Headers.ContentLength ?? 0;
            return cLength;
        }

        /// <summary>
        /// Downloads a file from Nintendo and saves it to the specified path.
        /// </summary>
        /// <param name="url"></param>
        /// <param name="fpath"></param>
        public async Task<bool> DownloadFile(string url, string fpath)
        {
            var finfo = new FileInfo(fpath);
            long downloaded = 0;
            long expectedSize = 0;
            FileStream fs;
            HttpResponseMessage result;

            if (finfo.Exists)
            {
                downloaded = finfo.Length;

                result = await MakeRequest(HttpMethod.Get, url, null, new Dictionary<string, string>() { { "Range", "bytes=" + downloaded + "-" } }, false).ConfigureAwait(false);

                if (!result.Headers.Server.First().ToString().Equals("openresty/1.9.7.4")) // Completed download
                {
                    Console.WriteLine("Download complete, skipping: " + fpath);
                    return true;
                }
                else if (result.Content.Headers.ContentRange == null) // CDN doesn't return a range if request >= filesize
                {
                    long cLength = result.Content.Headers.ContentLength ?? 0;
                    expectedSize = cLength;
                }
                else
                {
                    long cLength = result.Content.Headers.ContentLength ?? 0;
                    expectedSize = downloaded + cLength;
                }

                if (downloaded == expectedSize)
                {
                    Console.WriteLine("Download complete, skipping: " + fpath);
                    return true;
                }
                else if (downloaded < expectedSize)
                {
                    Console.WriteLine("Resuming previous download: " + fpath);
                    fs = File.Open(fpath, FileMode.Append, FileAccess.Write);
                }
                else
                {
                    Console.WriteLine("Existing file is larger than it should be, restarting: " + fpath);
                    downloaded = 0;
                    fs = File.Create(fpath);
                }

            }
            else
            {
                fs = File.Create(fpath);
                downloaded = 0;

                result = await MakeRequest(HttpMethod.Get, url, null, null, false);
                long cLength = result.Content.Headers.ContentLength ?? 0;
                expectedSize = cLength;
            }

            // this is where I download the file
            // I can either not have any progress indicators and just DO IT
            // Or I can  create some asynchronous class that maintains a download in a separate thread that communicates with a UI element

            // For now I'm going to do a basic download
            // ...
            // On second though, a bit of research brings up this await and async crap
            // It is confusing because you don't return stuff, you "await" an async task and that implicitly returns a Task
            // but otherwise you always just return with no argument
            // The thing that calls DownloadFile either uses "await" to wait for it to finish or it can 
            // collect tasks somewhere until they're done. Right? I don't actually know

            bool completed = await StartDownload(fs, result, expectedSize, downloaded).ConfigureAwait(false);

            fs.Dispose();
            result.Dispose();

            var newFile = new FileInfo(fpath);
            if (completed && expectedSize != 0 && newFile.Length != expectedSize)
            {
                throw new Exception("Downloaded file doesn't match expected size after download completion: " + newFile.FullName);
            }

            return completed;
        }

        public delegate void DownloadDelegate(DownloadTask download);
        public delegate void DownloadProgressDelegate(DownloadTask download, int progress);
        public event DownloadDelegate DownloadStarted;
        public event DownloadProgressDelegate DownloadProgress;
        public event DownloadDelegate DownloadFinished;

        /// <summary>
        /// Starts an async file download, which downloads the file in chunks and reports progress, as well
        /// as the start and end of the download.
        /// </summary>
        /// <param name="fileStream"></param>
        /// <param name="result"></param>
        /// <param name="expectedSize"></param>
        /// <returns>true if the download completed and false if it was cancelled</returns>
        private async Task<bool> StartDownload(FileStream fileStream, HttpResponseMessage result, long expectedSize, long startingSize = 0)
        {
            using (Stream remoteStream = await result.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                DownloadTask download = new DownloadTask(remoteStream, fileStream, expectedSize, startingSize);

                if (DownloadStarted != null) DownloadStarted.Invoke(download);

                byte[] buffer = new byte[this.DownloadBuffer];
                while (true)
                {
                    // Read from the web.
                    int n = await remoteStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);

                    if (n == 0 || download.IsCanceled)
                    {
                        // There is nothing else to read.
                        break;
                    }

                    // Report progress.
                    download.UpdateProgress(n);

                    if (DownloadStarted != null) DownloadProgress.Invoke(download, n);

                    // Write to file.
                    fileStream.Write(buffer, 0, n);
                }
                if (DownloadFinished != null) DownloadFinished.Invoke(download);
                fileStream.Flush();

                return !download.IsCanceled;
            }
        }

        /// <summary>
        /// TODO: Figure out what CETK is
        /// </summary>
        /// <param name="rightsID"></param>
        /// <param name="fpath"></param>
        /// <returns></returns>
        private async Task<bool> DownloadCETK(string rightsID, string fpath)
        {
            string url = $"https://atum.hac.{environment}.d4c.nintendo.net/r/t/{rightsID}?device_id={deviceId}";
            var head = await HeadRequest(url, null, null).ConfigureAwait(false);

            string cnmtid = GetHeader(head, "X-Nintendo-Content-ID");

            url = $"https://atum.hac.{environment}.d4c.nintendo.net/c/t/{cnmtid}?device_id={deviceId}";
            return await DownloadFile(url, fpath).ConfigureAwait(false);
        }

        /// <summary>
        /// Queries the CDN for all versions of a game
        /// </summary>
        /// <param name="game"></param>
        /// <returns></returns>
        public async Task<ObservableCollection<uint>> GetVersions(SwitchTitle game)
        {
            //string url = string.Format("https://tagaya.hac.{0}.eshop.nintendo.net/tagaya/hac_versionlist", env);
            string url = string.Format("https://superfly.hac.{0}.d4c.nintendo.net/v1/t/{1}/dv", environment, game.TitleID);
            string r = await GetRequest(url);

            JObject json = JObject.Parse(r);
            uint latestVersion = json?.Value<uint>("version") ?? 0;

            return GetAllVersions(latestVersion); ;
        }

        /// <summary>
        /// Converts a single version number into a list of all available versions.
        /// </summary>
        /// <param name="versionNo"></param>
        /// <returns></returns>
        public ObservableCollection<uint> GetAllVersions(uint versionNo)
        {
            var versions = new ObservableCollection<uint>();
            for (uint v = versionNo; v > 0; v -= 0x10000)
            {
                versions.Add(v);
            }

            versions.Add(0);
            return versions;
        }

        /// <summary>
        /// Gets ALL games' versions and required versions, whatever that means.
        /// format is {"format_version":1,"last_modified":1533248100}, "titles":[{"id":"01007ef00011e800","version":720896,"required_version":720896},...]}
        /// 
        /// Versions are 0, 0x10000, 0x20000, etc up to the listed number.
        /// </summary>
        /// <returns></returns>
        public async Task<Dictionary<string, uint>> GetLatestVersions()
        {
            string url = string.Format("https://tagaya.hac.{0}.eshop.nintendo.net/tagaya/hac_versionlist", environment);
            string r = await GetRequest(url).ConfigureAwait(false);

            JObject json = JObject.Parse(r);
            IList<JToken> titles = json["titles"].Children().ToList();

            var result = new Dictionary<string, uint>();
            foreach (var title in titles)
            {
                // Okay so I don't know why (perhaps this has something to do with word alignment? That's too deep in the weeds for me)
                // but every title id ends with 000, except in the results from here they all end with 800
                // Until I understand how it works I'm just going to swap the 8 for a 0.
                // Research update: see get_name_control in python sou rce
                // Titles ending in 000 are base game
                // Titles ending in 800 are updates (what about multiple updates?)
                // I guess this explains why the versions url gets titles ending in 800 - it is a list of updates,
                // and that also explains why titles with no update don't appear there.
                // The pattern for DLC is extra weird
                // TODO: Figure out DLC title ids
                // TODO: Figure out how that 800 works if there are multiple updates - do you do a request for XXX800, plus a version to get only the update file?
                // Does that mean that if you request XXX000 for base title, that the version number is irrelevant? Or do you get updates included in the nsp instead of separately?
                string tid = SwitchTitle.GetBaseGameIDFromUpdate(title.Value<string>("id"));
                uint latestVersion = title.Value<uint>("version");
                result[tid] = latestVersion;
            }
            return result;
        }

        private string GetHeader(HttpResponseHeaders headers, string name)
        {
            if (headers != null && headers.Contains(name))
            {
                IEnumerable<string> h = headers.GetValues(name);
                string result = h.First();
                return result;
            }

            return null;
        }

        /// <summary>
        /// Makes a request to Ninty's server, but we only care about getting back the content as a string.
        /// Always a GET request.
        /// /// </summary>
        /// <param name="url"></param>
        /// <param name="cert"></param>
        /// <param name="args"></param>
        private async Task<string> GetRequest(string url, X509Certificate cert, Dictionary<string, string> args = null)
        {
            var response = await MakeRequest(HttpMethod.Get, url, cert, args);
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// Makes a simple reqeust to Ninty's server, using the default client cert and no special headers.
        /// Always a GET request and returns content body as a string.
        /// /// </summary>
        /// <param name="url"></param>
        /// <param name="cert"></param>
        /// <param name="args"></param>
        private async Task<string> GetRequest(string url)
        {
            var response = await MakeRequest(HttpMethod.Get, url, null, null).ConfigureAwait(false);
            string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return result;
        }

        /// <summary>
        /// Makes a request to Ninty's server. Gets back only the response headers.
        /// </summary>
        /// <param name="url"></param>
        /// <param name="cert"></param>
        /// <param name="args"></param>
        private async Task<HttpResponseHeaders> HeadRequest(string url, X509Certificate cert = null, Dictionary<string, string> args = null)
        {
            var response = await MakeRequest(HttpMethod.Head, url, cert, args);
            return response.Headers;
        }

        private HttpClientHandler singletonHandler;
        private HttpClient singletonClient;
        public HttpClient GetSingletonClient(X509Certificate cert)
        {
            if (singletonClient == null)
            {
                string userAgent = string.Format($"NintendoSDK Firmware/{firmware} (platform:NX; eid:{environment})");


                // Add the client certificate
                singletonHandler = new HttpClientHandler
                {
                    ClientCertificateOptions = ClientCertificateOption.Manual,
                    //SslProtocols = SslProtocols.Tls12,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                };
                singletonHandler.ClientCertificates.Add(this.ClientCert);
                ServicePointManager.ServerCertificateValidationCallback += (o, c, ch, er) => true;
                ServicePointManager.DefaultConnectionLimit = 1000;

                // Create client and get response
                singletonClient = new HttpClient(singletonHandler);
                singletonClient.Timeout = TimeSpan.FromMinutes(30);
            }

            singletonHandler.ClientCertificates.Clear();
            if (cert == null)
                cert = this.ClientCert;
            singletonHandler.ClientCertificates.Add(cert);

            return singletonClient;
        }

        /// <summary>
        /// Makes a request to Ninty's server. Gets back the entire response.
        /// WHY THE FUCK IS THIS SO COMPLICATED???? JUST LET ME SEND A REQUEST
        /// </summary>
        /// <param name="method"></param>
        /// <param name="url"></param>
        /// <param name="cert"></param>
        /// <param name="args"></param>
        private async Task<HttpResponseMessage> MakeRequest(HttpMethod method, string url, X509Certificate cert = null, Dictionary<string, string> args = null, bool waitForContent = true)
        {
            string userAgent = string.Format($"NintendoSDK Firmware/{firmware} (platform:NX; did:{deviceId}; eid:{environment})");

            // Create request with method & url, then add headers
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("User-Agent", userAgent);
            request.Headers.Add("Accept-Encoding", "gzip, deflate");
            request.Headers.Add("Accept", "*/*");
            request.Headers.Add("Connection", "keep-alive");

            // Add any additional parameters passed into the method
            if (args != null) args.ToList().ForEach(x => request.Headers.Add(x.Key, x.Value));

            var client = GetSingletonClient(cert);
            if (waitForContent)
                return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
            else
                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }

        /// <summary>
        /// Get a game's CNMT ID, which you can use to download the CNMT NCA.
        /// </summary>
        /// <param name="title">Title to get the CNMD ID for.</param>
        /// <param name="version">Version of the title you want. ` </param>
        /// <returns></returns>
        private async Task<string> GetCnmtID(SwitchTitle title, uint version)
        {
            string url = $"https://atum.hac.{environment}.d4c.nintendo.net/t/a/{title.TitleID}/{version}?device_id={deviceId}";

            var head = await HeadRequest(url, null, null).ConfigureAwait(false);

            string cnmtid = GetHeader(head, "X-Nintendo-Content-ID");

            return cnmtid;
        }

        /// <summary>
        /// Downloads a CNMT NCA file from Nintendo's CDN.
        /// </summary>
        /// <param name="cnmtid">ID of the CNMT. Use GetCnmtId to find it.</param>
        /// <param name="path">Path of the downloaded file. This is where it will be once this function is completed.</param>
        /// <returns>FileInfo for the downloaded CNMT NCA.</returns>
        private async Task<bool> DownloadCnmt(string cnmtid, string path)
        {
            // Download cnmt file, async
            string url = $"https://atum.hac.{environment}.d4c.nintendo.net/c/a/{cnmtid}?device_id={deviceId}";
            return await DownloadFile(url, path).ConfigureAwait(false);
        }

        private async Task<CNMT> DownloadAndDecryptCnmt(SwitchTitle title, uint version, string titleDir)
        {
            // Get the CNMT ID for the title
            string cnmtid = await GetCnmtID(title, version).ConfigureAwait(false);
            if (cnmtid == null)
                throw new Exception($"No or invalid CNMT ID found for {title.Name} {title.TitleID}");

            // Path to the NCA
            string ncaPath = titleDir + Path.DirectorySeparatorChar + cnmtid + ".cnmt.nca";

            // Download the CNMT NCA file
            bool completed = await DownloadCnmt(cnmtid, ncaPath).ConfigureAwait(false);
            if (!completed) return null;

            // Decrypt the CNMT NCA file (all NCA files are encrypted by nintendo)
            // Hactool does the job for us
            DirectoryInfo cnmtDir = DecryptNCA(ncaPath);

            CNMT cnmt = GetDownloadedCnmt(cnmtDir, ncaPath);
            return cnmt;
        }

        private CNMT GetDownloadedCnmt(DirectoryInfo cnmtDir, string ncaPath)
        {
            // For CNMTs, there is a section0 containing a single cnmt file, plus a Header.bin right next to section0
            var sectionDirInfo = cnmtDir.EnumerateDirectories("section0").First();
            var extractedCnmt = sectionDirInfo.EnumerateFiles().First();
            var headerFile = cnmtDir.EnumerateFiles("Header.bin").First();

            return new CNMT(extractedCnmt.FullName, headerFile.FullName, cnmtDir.FullName, ncaPath);
        }

        /// <summary>
        /// Eshop requests do not work. I suspect it has something to do with sending them a bearer token,
        /// but I have no idea how to get that, short of trying to log in with my own account and copying the token,
        /// and I don't want to do that.
        /// </summary>
        /// <param name="title"></param>
        /// <param name="lang"></param>
        /// <returns></returns>
        public async Task<object> GetEshopData(SwitchTitle title, string lang)
        {
            string url = $"https://bugyo.hac.{environment}.eshop.nintendo.net/shogun/v1/contents/ids?shop_id=4&lang={lang}&country={region}&type=title&title_ids={title.TitleID}";

            var response = await GetRequest(url, EshopCert);

            return response;
        }

        public async Task<long> GetTitleSize(SwitchTitle title, uint version, string titleDir)
        {
            var cnmt = await DownloadAndDecryptCnmt(title, version, titleDir).ConfigureAwait(false);

            if (cnmt != null)
            {
                // Now that the CNMT NCA was downloaded and decrypted, read it f
                string ticketPath = this.titleTicketPath, certPath = this.titleCertPath;
                string cnmtXml = titleDir + Path.DirectorySeparatorChar + Path.GetFileNameWithoutExtension(cnmt.CnmtNcaFilePath) + ".xml";
                cnmt.GenerateXml(cnmtXml);

                List<Task<long>> tasks = new List<Task<long>>();

                NSP nsp = new NSP(title, certPath, ticketPath, cnmt.CnmtNcaFilePath, cnmtXml);
                foreach (var type in new[] { NCAType.Meta, NCAType.Control, NCAType.HtmlDocument, NCAType.LegalInformation, NCAType.Program, NCAType.Data, NCAType.DeltaFragment })
                {
                    // To verify, we need to parse the CNMT more thoroughly, which is a waste of effort if we aren't verifying
                    
                    var parsedNCAFiles = cnmt.ParseNCAs(type);
                    foreach (var ncaID in parsedNCAFiles)
                    {
                        string url = $"https://atum.hac.{environment}.d4c.nintendo.net/c/c/{ncaID}?device_id={deviceId}";
                        Task<long> t = GetContentLength(url);
                        tasks.Add(t);
                    }
                }

                long[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return new FileInfo(cnmt.CnmtFilePath).Length +
                    new FileInfo(cnmtXml).Length +
                    new FileInfo(certPath).Length +
                    new FileInfo(ticketPath).Length +
                    NSP.GenerateHeader(nsp.Files.ToArray()).Length +
                    results.Sum();
            }

            return 0;
        }
    }
}
                