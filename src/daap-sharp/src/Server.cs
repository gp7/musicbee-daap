/*
 * daap-sharp
 * Copyright (C) 2005  James Willcox <snorp@snorp.net>
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using Mono.Zeroconf;

using MusicBeePlugin;

namespace DAAP
{

    internal delegate bool WebHandler(Socket client, string userAgent, string user, string path, NameValueCollection query, int range);

    internal class WebServer {

        private const int ChunkLength = 8192;

        private UInt16 port;
        private Socket server;
        private WebHandler handler;
        private bool running;
        private List<NetworkCredential> creds = new List<NetworkCredential>();
        private ConcurrentDictionary<IntPtr, Socket> clients = new ConcurrentDictionary<IntPtr, Socket>();
        private string realm;
        private AuthenticationMethod authMethod = AuthenticationMethod.None;

        public ushort RequestedPort {
            get { return port; }
            set { port = value; }
        }

        public ushort BoundPort {
            get { return (ushort)(server.LocalEndPoint as IPEndPoint).Port; }
        }

        public IList<NetworkCredential> Credentials {
            get { return new ReadOnlyCollection<NetworkCredential>(creds); }
        }

        public AuthenticationMethod AuthenticationMethod {
            get { return authMethod; }
            set { authMethod = value; }
        }

        public string Realm {
            get { return realm; }
            set { realm = value; }
        }

        public WebServer(UInt16 port, WebHandler handler) {
            this.port = port;
            this.handler = handler;
        }

        public void Start() {
            server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
            server.Bind(new IPEndPoint(IPAddress.Any, port));
            server.Listen(10);

            running = true;
            Thread thread = new Thread(ServerLoop);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop() {
            running = false;

            if (server != null) {
                server.Close();
                server = null;
            }
            
            foreach (KeyValuePair<IntPtr, Socket> clientEntry in clients.ToArray()) {
                clientEntry.Value.Close();
            }
        }

        public void AddCredential(NetworkCredential cred) {
            creds.Add(cred);
        }

        public void RemoveCredential(NetworkCredential cred) {
            creds.Remove(cred);
        }

        public void WriteResponse(Socket client, ContentNode node) {
            WriteResponse(client, HttpStatusCode.OK,
                           ContentWriter.Write(ContentCodeBag.Default, node));
        }

        public void WriteResponse(Socket client, HttpStatusCode code, string body) {
            WriteResponse(client, code, Encoding.UTF8.GetBytes(body));
        }

        public void WriteResponse(Socket client, HttpStatusCode code, byte[] body) {
            if (!client.Connected)
                return;

            using (BinaryWriter writer = new BinaryWriter(new NetworkStream(client, false))) {
                writer.Write(Encoding.UTF8.GetBytes(String.Format("HTTP/1.1 {0} {1}\r\n", (int)code, code.ToString())));
                writer.Write(Encoding.UTF8.GetBytes("DAAP-Server: musicbee\r\n"));
                writer.Write(Encoding.UTF8.GetBytes("Content-Type: application/x-dmap-tagged\r\n"));
                writer.Write(Encoding.UTF8.GetBytes(String.Format("Content-Length: {0}\r\n", body.Length)));
                writer.Write(Encoding.UTF8.GetBytes("\r\n"));
                writer.Write(body);
            }
        }

        public void WriteResponseFile(Socket client, string file, long offset) {
            Stream stream = AudioStream.Open(file);
            WriteResponseStream(client, stream, stream.Length, offset);
        }


        internal void WriteResponseArtwork(Socket client, string trackFile)
        {
            using (ArtworkData data = Artwork.GetArtwork(trackFile)) {
                if (data == null) {
                    WriteResponse(client, HttpStatusCode.InternalServerError, "no file");
                } else {
                    string contentType = "image/" + data.type;
                    WriteResponseStream(client, data.data, data.data.Length, 0, contentType);
                }
            }
        }

        public void WriteResponseStream(Socket client, Stream response, long len) {
            WriteResponseStream(client, response, len, -1);
        }

        public void WriteResponseStream(Socket client, Stream response, long len, long offset, string contentType = null) {
            using (BinaryWriter writer = new BinaryWriter(new NetworkStream(client, false))) {

                if (offset > 0) {
                    writer.Write(Encoding.UTF8.GetBytes("HTTP/1.1 206 Partial Content\r\n"));
                    writer.Write(Encoding.UTF8.GetBytes(String.Format("Content-Range: bytes {0}-{1}/{2}\r\n",
                                                                         offset, len, len + 1)));
                    writer.Write(Encoding.UTF8.GetBytes("Accept-Range: bytes\r\n"));
                    len = len - offset;
                } else {
                    writer.Write(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\n"));
                }

                if (contentType != null) {
                    writer.Write(Encoding.UTF8.GetBytes(String.Format("Content-Type: {0}\r\n", contentType)));
                }

                writer.Write(Encoding.UTF8.GetBytes(String.Format("Content-Length: {0}\r\n", len)));
                writer.Write(Encoding.UTF8.GetBytes("\r\n"));

                using (BinaryReader reader = new BinaryReader(response)) {
                    if (offset > 0) {
                        reader.BaseStream.Seek(offset, SeekOrigin.Begin);
                    }

                    long count = 0;
                    while (count < len) {
                        byte[] buf = reader.ReadBytes(Math.Min(ChunkLength, (int)len - (int)count));
                        if (buf.Length == 0) {
                            break;
                        }

                        writer.Write(buf);
                        count += buf.Length;
                    }
                }
            }
        }

        public void WriteAccessDenied(Socket client) {
            string msg = "Authorization Required";

            using (BinaryWriter writer = new BinaryWriter(new NetworkStream(client, false))) {
                writer.Write(Encoding.UTF8.GetBytes("HTTP/1.1 401 Denied\r\n"));
                writer.Write(Encoding.UTF8.GetBytes(String.Format("WWW-Authenticate: Basic realm=\"{0}\"",
                                                                     realm)));
                writer.Write(Encoding.UTF8.GetBytes("Content-Type: text/plain\r\n"));
                writer.Write(Encoding.UTF8.GetBytes(String.Format("Content-Length: {0}\r\n", msg.Length)));
                writer.Write(Encoding.UTF8.GetBytes("\r\n"));
                writer.Write(msg);
            }
        }

        private bool IsValidAuth(string user, string pass) {
            if (authMethod == AuthenticationMethod.None)
                return true;

            foreach (NetworkCredential cred in creds) {

                if ((authMethod != AuthenticationMethod.UserAndPassword || cred.UserName == user) &&
                    cred.Password == pass)
                    return true;
            }

            return false;
        }

        private bool HandleRequest(Socket client) {

            if (!client.Connected)
                return false;

            bool ret = true;

            using (StreamReader reader = new StreamReader(new NetworkStream(client, false))) {

                string request = reader.ReadLine();
                if (request == null)
                    return false;

                string line = null;
                string user = null;
                string password = null;
                string userAgent = null;
                int range = -1;

                // read the rest of the request
                do {
                    line = reader.ReadLine();

                    if (line == null) {
                        break;
                    } else if (line == "Connection: close") {
                        ret = false;
                    } else if (line.StartsWith("Authorization: Basic")) {
                        string[] splitLine = line.Split(' ');

                        if (splitLine.Length != 3)
                            continue;

                        string userpass = Encoding.UTF8.GetString(Convert.FromBase64String(splitLine[2]));

                        string[] splitUserPass = userpass.Split(new char[] { ':' }, 2);
                        user = splitUserPass[0];
                        password = splitUserPass[1];
                    } else if (line.StartsWith("Range: ")) {
                        // we currently expect 'Range: bytes=<offset>-'
                        string[] splitLine = line.Split('=');

                        if (splitLine.Length != 2)
                            continue;

                        string rangestr = splitLine[1];
                        if (!rangestr.EndsWith("-"))
                            continue;

                        try {
                            range = Int32.Parse(rangestr.Substring(0, rangestr.Length - 1));
                        } catch (FormatException) {
                        }
                    } else if (line.StartsWith("User-Agent: ")) {
                        const int userAgentOffsetIndex = 12;
                        int platformIndex = 0;

                        do {
                            platformIndex = line.IndexOf('(', userAgentOffsetIndex + platformIndex);
                        } while (platformIndex == -1 || line[platformIndex - 1] != ' ');

                        if (platformIndex > userAgentOffsetIndex && platformIndex < line.Length) {
                            userAgent = line.Substring(userAgentOffsetIndex, platformIndex - userAgentOffsetIndex - 1);
                        }
                    }
                } while (line != String.Empty);


                string[] splitRequest = request.Split();
                if (splitRequest.Length < 3) {
                    WriteResponse(client, HttpStatusCode.BadRequest, "Bad Request");
                } else {
                    try {
                        string path = splitRequest[1];
                        if (!path.StartsWith("daap://")) {
                            path = String.Format("daap://localhost{0}", path);
                        }

                        Uri uri = new Uri(path);
                        NameValueCollection query = new NameValueCollection();

                        if (uri.Query != null && uri.Query != String.Empty) {
                            string[] splitquery = uri.Query.Substring(1).Split('&');

                            foreach (string queryItem in splitquery) {
                                if (queryItem == String.Empty)
                                    continue;

                                string[] splitQueryItem = queryItem.Split('=');
                                query[splitQueryItem[0]] = splitQueryItem[1];
                            }
                        }

                        if (authMethod != AuthenticationMethod.None && uri.AbsolutePath == "/login" &&
                            !IsValidAuth(user, password)) {
                            WriteAccessDenied(client);
                            return true;
                        }

                        return handler(client, userAgent, user, uri.AbsolutePath, query, range);
                    } catch (IOException) {
                        ret = false;
                    } catch (Exception e) {
                        ret = false;
                        Console.Error.WriteLine("Trouble handling request {0}: {1}", splitRequest[1], e);
                    }
                }
            }

            return ret;
        }

        private void HandleConnection(object o) {
            Socket client = (Socket)o;

            try {
                while (HandleRequest(client)) { }
            } catch (IOException) {
                // ignore
            } catch (Exception e) {
                Console.Error.WriteLine("Error handling request: " + e);
            } finally {
                Socket removed = null;
                clients.TryRemove(client.Handle, out removed);
                System.Diagnostics.Debug.Assert(client == removed);
                client.Close();
            }
        }

        private void ServerLoop() {
            while (true) {
                try {
                    if (!running)
                        break;

                    Socket client = server.Accept();
                    bool added = clients.TryAdd(client.Handle, client);
                    System.Diagnostics.Debug.Assert(added);
                    ThreadPool.QueueUserWorkItem(HandleConnection, client);
                } catch (SocketException) {
                    break;
                }
            }
        }
    }

    public class TrackRequestedArgs : EventArgs {

        private string user;
        private IPAddress host;
        private MusicBeeDatabase db;
        private Track track;

        public string UserName {
            get { return user; }
        }

        public IPAddress Host {
            get { return host; }
        }

        public MusicBeeDatabase Database {
            get { return db; }
        }

        public Track Track {
            get { return track; }
        }

        public TrackRequestedArgs(string user, IPAddress host, MusicBeeDatabase db, Track track) {
            this.user = user;
            this.host = host;
            this.db = db;
            this.track = track;
        }
    }

    public class DatabaseRequestedArgs : EventArgs
    {
        public string userAgent;
        public string daapMetadata;
        public byte[] response;
    }
    
    public delegate void TrackRequestedHandler (object o, TrackRequestedArgs args);
    public delegate void DatabaseRequestedHandler(object o, DatabaseRequestedArgs args);

    public class Server {

        internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes (30);
        private static readonly int databaseCount = 1;

        private static Regex dbItemsRegex = new Regex ("/databases/([0-9]*?)/items$");
        private static Regex dbTrackRegex = new Regex ("/databases/([0-9]*?)/items/([0-9]*).*");
        private static Regex dbContainersRegex = new Regex ("/databases/([0-9]*?)/containers$");
        private static Regex dbContainerItemsRegex = new Regex ("/databases/([0-9]*?)/containers/([0-9]*?)/items$");
        private static Regex dbExtraDataRegex = new Regex("/databases/([0-9]*?)/items/([0-9]*?)/extra_data/artwork$");

        private WebServer ws;
        private Dictionary<int, User> sessions = new Dictionary<int, User> ();
        private Random random = new Random ();
        private UInt16 port = 3689;
        private ServerInfo serverInfo = new ServerInfo ();
        private bool publish = true;
        private int maxUsers = 0;
        private bool running;
        private string machineId;

        private RegisterService zc_service;
        private MusicBeeRevisionManager revmgr;
        private MusicBeeDatabase musicBeeDb;

        private object eglock = new object ();

        public event EventHandler Collision;
        public event TrackRequestedHandler TrackRequested;
        public event DatabaseRequestedHandler DatabaseRequested;
        public event UserHandler UserLogin;
        public event UserHandler UserLogout;

        public IList<User> Users {
            get {
                lock (sessions) {
                    return new ReadOnlyCollection<User> (new List<User> (sessions.Values));
                }
            }
        }

        public string Name {
            get { return serverInfo.Name; }
            set {
                serverInfo.Name = value;
                ws.Realm = value;

                if (publish)
                    RegisterService ();
            }
        }

        public string MachineId {
            get { return machineId; }
            set { machineId = value; }
        }

        public UInt16 Port {
            get { return port; }
            set {
                port = value;
                ws.RequestedPort = value;
            }
        }

        public bool IsPublished {
            get { return publish; }
            set {
                publish = value;

                if (running && publish)
                    RegisterService ();
                else if (running && !publish)
                    UnregisterService ();
            }
        }

        public bool IsRunning {
            get { return running; }
        }

        public AuthenticationMethod AuthenticationMethod {
            get { return serverInfo.AuthenticationMethod; }
            set {
                serverInfo.AuthenticationMethod = value;
                ws.AuthenticationMethod = value;
            }
        }

        public IList<NetworkCredential> Credentials {
            get { return ws.Credentials; }
        }

        public int MaxUsers {
            get { return maxUsers; }
            set { maxUsers = value; }
        }

        public Server (string name, MusicBeeDatabase db, MusicBeeRevisionManager revisionManager) {
            ws = new WebServer (port, OnHandleRequest);
            musicBeeDb = db;
            revmgr = revisionManager;
            serverInfo.Name = name;
            ws.Realm = name;
        }

        public void Start () {
            running = true;
            ws.Start ();

            if (publish)
                RegisterService ();
        }

        public void Stop () {
            running = false;

            ws.Stop ();
            UnregisterService ();
        }
        
        public void AddCredential (NetworkCredential cred) {
            ws.AddCredential (cred);
        }

        public void RemoveCredential (NetworkCredential cred) {
            ws.RemoveCredential (cred);
        }

        private void RegisterService () {
            lock (eglock) {
                if (zc_service != null) {
                    UnregisterService ();
                }

                string auth = serverInfo.AuthenticationMethod == AuthenticationMethod.None ? "false" : "true";

                zc_service = new RegisterService ();
                zc_service.Name = serverInfo.Name;
                zc_service.RegType = "_daap._tcp";
                zc_service.Port = (short)ws.BoundPort;
                zc_service.TxtRecord = new TxtRecord ();
                zc_service.TxtRecord.Add ("Password", auth);
                zc_service.TxtRecord.Add ("Machine Name", serverInfo.Name);

                if (machineId != null) {
                    zc_service.TxtRecord.Add ("Machine ID", machineId);
                }

                zc_service.TxtRecord.Add ("txtvers", "1");
                zc_service.Response += OnRegisterServiceResponse;
                zc_service.Register();
            }
        }

        private void UnregisterService () {
            lock (eglock) {
                if (zc_service == null) {
                    return;
                }

                try {
                    zc_service.Dispose ();
                } catch {
                }
                zc_service = null;
            }
        }

        private void OnRegisterServiceResponse (object o, RegisterServiceEventArgs args) {
            if (args.ServiceError == ServiceErrorCode.AlreadyRegistered && Collision != null) {
                Collision (this, new EventArgs ());
            }
        }

        private void ExpireSessions () {
            lock (sessions) {
                foreach (int s in new List<int> (sessions.Keys)) {
                    User user = sessions[s];

                    if (DateTime.Now - user.LastActionTime > DefaultTimeout) {
                        sessions.Remove (s);
                        OnUserLogout (user);
                    }
                }
            }
        }

        private void OnUserLogin (User user) {
            UserHandler handler = UserLogin;
            if (handler != null) {
                try {
                    handler (this, new UserArgs (user));
                } catch (Exception e) {
                    Console.Error.WriteLine ("Exception in UserLogin event handler: " + e);
                }
            }
        }

        private void OnUserLogout (User user) {
            UserHandler handler = UserLogout;
            if (handler != null) {
                try {
                    handler (this, new UserArgs (user));
                } catch (Exception e) {
                    Console.Error.WriteLine ("Exception in UserLogout event handler: " + e);
                }
            }
        }

        internal bool OnHandleRequest (Socket client, string userAgent, string username, string path, NameValueCollection query, int range) {
            System.Diagnostics.Debug.WriteLine(path);

            int session = 0;
            if (query["session-id"] != null) {
                session = Int32.Parse (query["session-id"]);
            }

            if (!sessions.ContainsKey (session) && path != "/server-info" && path != "/content-codes" &&
                path != "/login") {
                ws.WriteResponse (client, HttpStatusCode.Forbidden, "invalid session id");
                return true;
            }

            if (session != 0) {
                sessions[session].LastActionTime = DateTime.Now;
            }

            int clientRev = 0;
            if (query["revision-number"] != null) {
                clientRev = Int32.Parse (query["revision-number"]);
            }

            int delta = 0;
            if (query["delta"] != null) {
                delta = Int32.Parse (query["delta"]);
            }

            if (path == "/server-info") {
                ws.WriteResponse(client, GetServerInfoNode());
            } else if (path == "/content-codes") {
                ws.WriteResponse(client, ContentCodeBag.Default.ToNode());
            } else if (path == "/login") {
                ExpireSessions();

                if (maxUsers > 0 && sessions.Count + 1 > maxUsers) {
                    ws.WriteResponse(client, HttpStatusCode.ServiceUnavailable, "too many users");
                    return true;
                }

                session = random.Next();
                User user = new User(DateTime.Now, (client.RemoteEndPoint as IPEndPoint).Address, username);

                lock (sessions) {
                    sessions[session] = user;
                }

                ws.WriteResponse(client, GetLoginNode(session));
                OnUserLogin(user);
            } else if (path == "/logout") {
                User user = sessions[session];

                lock (sessions) {
                    sessions.Remove(session);
                }

                ws.WriteResponse(client, HttpStatusCode.OK, new byte[0]);
                OnUserLogout(user);

                return false;
            } else if (path == "/databases") {
                ws.WriteResponse(client, GetDatabasesNode());
            } else if (dbExtraDataRegex.IsMatch(path)) {
                Match match = dbExtraDataRegex.Match(path);
                int dbid = Int32.Parse(match.Groups[1].Value);
                int trackid = Int32.Parse(match.Groups[2].Value);

                if (musicBeeDb.Id != dbid) {
                    ws.WriteResponse(client, HttpStatusCode.BadRequest, "invalid database id");
                    return true;
                }

                Track track = musicBeeDb.LookupTrackById(trackid);
                if (track == null) {
                    ws.WriteResponse(client, HttpStatusCode.BadRequest, "invalid track id");
                    return true;
                }

                try {
                    ws.WriteResponseArtwork(client, track.FileName);
                } finally {
                    client.Close();
                }
            } else if (dbItemsRegex.IsMatch(path)) {
                int dbid = Int32.Parse(dbItemsRegex.Match(path).Groups[1].Value);
                string daapMeta = query["meta"];
                
                if (musicBeeDb.Id != dbid) {
                    ws.WriteResponse(client, HttpStatusCode.BadRequest, "invalid database id");
                    return true;
                }

                int[] deletedIds = { };

                if (delta > 0) {
                    deletedIds = revmgr.GetDeletedIds(clientRev - delta);
                }

                byte[] response = musicBeeDb.ToTracksNodeBytes(daapMeta, deletedIds);

                try {
                    if (DatabaseRequested != null) {
                        DatabaseRequested(this, new DatabaseRequestedArgs { userAgent = userAgent, daapMetadata = daapMeta, response = response });
                    }
                } catch { }
                
                ws.WriteResponse(client, HttpStatusCode.OK, response);
            } else if (dbTrackRegex.IsMatch(path)) {
                Match match = dbTrackRegex.Match(path);
                int dbid = Int32.Parse(match.Groups[1].Value);
                int trackid = Int32.Parse(match.Groups[2].Value);

                if (musicBeeDb.Id != dbid) {
                    ws.WriteResponse(client, HttpStatusCode.BadRequest, "invalid database id");
                    return true;
                }

                Track track = musicBeeDb.LookupTrackById(trackid);
                if (track == null) {
                    ws.WriteResponse(client, HttpStatusCode.BadRequest, "invalid track id");
                    return true;
                }

                try {
                    try {
                        if (TrackRequested != null)
                            TrackRequested(this, new TrackRequestedArgs(username,
                                                                        (client.RemoteEndPoint as IPEndPoint).Address,
                                                                        musicBeeDb, track));
                    } catch { }

                    if (track.FileName != null) {
                        ws.WriteResponseFile(client, track.FileName, range);
                    } else {
                        ws.WriteResponse(client, HttpStatusCode.InternalServerError, "no file");
                    }
                } finally {
                    client.Close();
                }
            } else if (dbContainersRegex.IsMatch(path)) {
                int dbid = Int32.Parse(dbContainersRegex.Match(path).Groups[1].Value);

                if (musicBeeDb.Id != dbid) {
                    ws.WriteResponse(client, HttpStatusCode.BadRequest, "invalid database id");
                    return true;
                }

                ws.WriteResponse(client, musicBeeDb.ToPlaylistsNode());
            } else if (dbContainerItemsRegex.IsMatch(path)) {
                Match match = dbContainerItemsRegex.Match(path);
                int dbid = Int32.Parse(match.Groups[1].Value);
                int plid = Int32.Parse(match.Groups[2].Value);

                if (musicBeeDb.Id != dbid) {
                    ws.WriteResponse(client, HttpStatusCode.BadRequest, "invalid database id");
                    return true;
                }

                IPlaylist curpl = musicBeeDb.LookupPlaylistById(plid);
                if (curpl == null) {
                    ws.WriteResponse(client, HttpStatusCode.BadRequest, "invalid playlist id");
                    return true;
                }

                int[] deletedIds = { };
                if (delta > 0) {
                    deletedIds = curpl.GetDeletedIds(clientRev - delta);
                }
                
                ws.WriteResponse(client, curpl.ToTracksNode(deletedIds));
            } else if (path == "/update") {
                int retrev = revmgr.WaitForUpdate(clientRev);
                
                if (!running) {
                    ws.WriteResponse(client, HttpStatusCode.NotFound, "server has been stopped");
                } else {
                    ws.WriteResponse(client, GetUpdateNode(retrev));
                }
            } else {
                ws.WriteResponse(client, HttpStatusCode.Forbidden, "");
            }

            return true;
        }

        private ContentNode GetLoginNode (int id) {
            return new ContentNode ("dmap.loginresponse",
                                    new ContentNode ("dmap.status", 200),
                                    new ContentNode ("dmap.sessionid", id));
        }

        private ContentNode GetServerInfoNode () {
            return serverInfo.ToNode (databaseCount); // Number of databases
        }

        private ContentNode GetDatabasesNode () {
            ArrayList databaseNodes = new ArrayList ();
            
            databaseNodes.Add(musicBeeDb.ToDatabaseNode());

            ContentNode node = new ContentNode ("daap.serverdatabases",
                                                new ContentNode ("dmap.status", 200),
                                                new ContentNode ("dmap.updatetype", (byte) 0),
                                                new ContentNode ("dmap.specifiedtotalcount", databaseCount),
                                                new ContentNode ("dmap.returnedcount", databaseCount),
                                                new ContentNode ("dmap.listing", databaseNodes));

            return node;
        }

        private ContentNode GetUpdateNode (int revision) {
            return new ContentNode ("dmap.updateresponse",
                                    new ContentNode ("dmap.status", 200),
                                    new ContentNode ("dmap.serverrevision", revision));
        }
    }
}
