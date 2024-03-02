﻿using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;



public class Server
{
    byte maxPlayers;

    static Socket serverTcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    static Socket serverUdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    static ConnectedPlayer[] connectedPlayers;

    int sentBytesPerSecond = 0;
    int receivedBytesPerSecond = 0;

    static bool encryption = true;
    byte[] encryptionKey = new byte[32];

    public Server(byte maxPlayers, int port)
    {
        this.maxPlayers = maxPlayers;
        int tcpPort = port;
        int udpPort = port + 1;

        // Starts TCP server
        serverTcpSocket.Bind(new IPEndPoint(IPAddress.Any, tcpPort));
        serverTcpSocket.Listen();

        // Starts UDP server
        serverUdpSocket.Bind(new IPEndPoint(IPAddress.Any, udpPort));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Due to this issue: https://stackoverflow.com/questions/74327225/why-does-sending-via-a-udpclient-cause-subsequent-receiving-to-fail
            // .. the following needs to be done on windows
            const uint IOC_IN = 0x80000000U;
            const uint IOC_VENDOR = 0x18000000U;
            const int SIO_UDP_CONNRESET = unchecked((int)(IOC_IN | IOC_VENDOR | 12));
            serverUdpSocket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0x00 }, null);
        }

        InitializeValues(maxPlayers);
        GetEncryptionKey();

        Database.Initialize();

        Task.Run(() => WaitForNewConnections());
        Task.Run(() => ReceiveUdpData());
        Task.Run(() => ReplicatePlayerPositions());
        Task.Run(() => RunEverySecond(tcpPort, udpPort));
        Thread.Sleep(Timeout.Infinite);
    }
    void InitializeValues(int maxPlayers)
    {
        connectedPlayers = new ConnectedPlayer[maxPlayers];
    }
    void GetEncryptionKey()
    {
        string path = "encryption_key.txt";

        if (!File.Exists(path))
        {
            File.Create(path).Dispose();

            using (TextWriter writer = new StreamWriter(path))
            {
                string keyString = "0123456789ABCDEF0123456789ABCDEF";
                encryptionKey = Encoding.ASCII.GetBytes(keyString);
                writer.WriteLine(keyString); // default encryption key
                writer.Close();
            }

        }
        else if (File.Exists(path))
        {
            using (TextReader reader = new StreamReader(path))
            {
                string keyString = reader.ReadLine();

                encryptionKey = Encoding.ASCII.GetBytes(keyString);
                reader.Close();
            }
        }
    }
    async Task WaitForNewConnections()
    {
        Byte[] buffer = new Byte[512];
        int bytesReceived;
        while (true)
        {
            using (Socket newClientSocket = await serverTcpSocket.AcceptAsync())
            {
                bytesReceived = await newClientSocket.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
                await ProcessBuffer(buffer, bytesReceived, newClientSocket, null); // Processes the received packet
            }
        }
    }
    async Task ReceiveTcpData(ConnectedPlayer connectedClient)
    {
        try
        {
            CancellationToken cancellationToken = connectedClient.cancellationTokenSource.Token;
            Socket clientTcpSocket = connectedClient.tcpSocket;

            Byte[] buffer = new Byte[1024];
            int bytesReceived;
            while (!cancellationToken.IsCancellationRequested)
            {
                bytesReceived = await clientTcpSocket.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None, cancellationToken);
                await ProcessBuffer(buffer, bytesReceived, null, connectedClient);
            }
        }
        catch (OperationCanceledException)
        {
            System.Console.WriteLine($"Receiving task for client id {connectedClient.index} was cancelled");
        }
        catch (IOException ex) when (ex.InnerException is SocketException socketEx && socketEx.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Handle sudden client disconnect (ConnectionReset)
            Console.WriteLine($"Client disconnected abruptly: {connectedClient.ipAddress}");
        }
        // catch (Exception e)
        // {
        //     Console.WriteLine("fail");
        // }
    }
    async Task ReceiveUdpData()
    {
        try
        {
            byte[] buffer = new byte[1024];
            while (true)
            {
                EndPoint udpEndPoint = new IPEndPoint(IPAddress.Any, 0);

                int bytesRead = serverUdpSocket.ReceiveFrom(buffer, ref udpEndPoint);

                ConnectedPlayer connectedPlayer = CheckAuthenticationOfUdpClient(udpEndPoint);
                if (connectedPlayer != null)
                {
                    await ProcessBuffer(buffer, bytesRead, null, connectedPlayer);
                }
            }
        }
        catch
        {
            Console.WriteLine("Error receiving UDP packet");
        }
    }
    ConnectedPlayer CheckAuthenticationOfUdpClient(EndPoint udpEndpoint) // checks if udp package sender is an actual player
    {
        IPEndPoint udpIpEndpoint = udpEndpoint as IPEndPoint;
        if (udpIpEndpoint == null) return null;

        foreach (ConnectedPlayer player in connectedPlayers)
        {
            if (player == null) continue;

            if (player.ipAddress.Equals(udpIpEndpoint.Address) && player.udpEndpoint == null) // checks if udp packet sender is authenticated player
            {
                if (player.udpPort == 0) // if authenticated but its the first package, get its port and assign udp endpoint to authenticated player
                {
                    player.udpPort = udpIpEndpoint.Port;
                    player.udpEndpoint = udpEndpoint;
                }
                return player;
            }
            else if (player.udpEndpoint.Equals(udpEndpoint))
            {
                return player;
            }
        }
        return null;
    }
    async Task ReplicatePlayerPositions()
    {
        int tickrate = 10;

        EveryPlayersPosition everyPlayersPosition = new EveryPlayersPosition(); // this thing is the format the server sends player positions in to each client
        everyPlayersPosition.p = new PlayerPosition[maxPlayers];

        string jsonData;
        while (true)
        {
            Thread.Sleep(tickrate); // server tick, 100 times a second
            for (byte i = 0; i < maxPlayers; i++) // copies the players' positions so server can send
            {
                if (connectedPlayers[i] == null)
                {
                    if (everyPlayersPosition.p[i] != null)
                    {
                        everyPlayersPosition.p[i] = null;
                    }
                    continue;
                }

                everyPlayersPosition.p[i] = connectedPlayers[i].position;
            }

            for (byte i = 0; i < maxPlayers; i++) // loops through every connected players positions to each
            {
                if (connectedPlayers[i] == null) continue; // skips index if no connected player occupies the slot
                //if (connectedPlayers[i].pingAnswered == false) continue;
                jsonData = JsonSerializer.Serialize(everyPlayersPosition, EveryPlayersPositionContext.Default.EveryPlayersPosition);
                await SendUdp(3, jsonData, connectedPlayers[i].udpEndpoint);
            }
        }
    }
    async Task RunEverySecond(int tcpPort, int udpPort)
    {
        //const byte timeoutTime = 4;

        while (true)
        {
            receivedBytesPerSecond = 0;
            sentBytesPerSecond = 0;
            //MonitorValues(tcpPort, udpPort);
            //await PingClients();
            Thread.Sleep(1000);
        }
    }
    void MonitorValues(int tcpPort, int udpPort)
    {
        Console.Clear();
        Console.WriteLine($"TCP port: {tcpPort}, UDP port: {udpPort} | Players: {GetCurrentPlayerCount()}/{maxPlayers}");
        Console.WriteLine($"Received bytes/s: {receivedBytesPerSecond}");
        Console.WriteLine($"Sent bytes/s: {sentBytesPerSecond}\n");
        for (byte i = 0; i < maxPlayers; i++)
        {
            Console.Write($"{i}: ");
            if (connectedPlayers[i] == null) { Console.WriteLine("Free slot"); continue; }
            Console.WriteLine(connectedPlayers[i]);
        }
    }
    //async Task PingClients()
    //{
    //    for (byte i = 0; i < maxPlayers; i++)
    //    {
    //        if (connectedPlayers[i] == null) continue;
    //        connectedPlayers[i].tcpSocket.ReceiveTimeout = 1000;
    //        if (connectedPlayers[i].tcpSocket.Poll(1000, SelectMode.SelectRead))
    //        {
    //            Console.WriteLine("Client answered");
    //        }
    //        else
    //        {
    //            Console.WriteLine("Client did not answer");
    //        }
    //    }

    //}
    //async Task PingClients(byte timeoutTime)
    //{
    //    for (byte i = 0; i < maxPlayers; i++)
    //    {
    //        try
    //        {
    //            if (connectedPlayers[i] == null) continue;

    //            if (connectedPlayers[i].pingAnswered == false) // runs if connected client hasn't replied to ping
    //            {
    //                connectedPlayers[i].timeUntillTimeout--;
    //                connectedPlayers[i].status = 0;

    //                if (connectedPlayers[i].timeUntillTimeout < 1) // runs if client didnt answer during timeout interval
    //                {
    //                    DisconnectClient(connectedPlayers[i].index);
    //                    continue;
    //                }
    //            }
    //            else if (connectedPlayers[i].pingAnswered == true && connectedPlayers[i].timeUntillTimeout != timeoutTime) // runs if connected client answered the ping
    //            {
    //                connectedPlayers[i].timeUntillTimeout = timeoutTime;
    //            }

    //            connectedPlayers[i].pingAnswered = false; // resets the array
    //            await SendUdp(0, "", connectedPlayers[i].udpEndpoint);
    //            connectedPlayers[i].pingRequestTime = DateTime.UtcNow;
    //        }
    //        catch (Exception ex)
    //        {
    //            Console.WriteLine(ex);
    //        }

    //    }
    //}
    byte GetCurrentPlayerCount()
    {
        byte playerCount = 0;
        for (byte i = 0; i < maxPlayers; i++)
        {
            if (connectedPlayers[i] != null)
            {
                playerCount++;
            }
        }
        return playerCount;
    }
    void CalculatePlayerLatency(byte clientIndex)
    {
        TimeSpan latency = connectedPlayers[clientIndex].pingRequestTime - DateTime.UtcNow;
        connectedPlayers[clientIndex].latency = Math.Abs(latency.Milliseconds) / 2;
    }
    void DisconnectClient(byte clientIndex)
    {
        Console.WriteLine($"Player {connectedPlayers[clientIndex].playerName} has disconnected");
        //clientSlotTaken[clientIndex] = false; // Free a slot

        //connectedPlayers[clientIndex].tcpSocket.Shutdown(SocketShutdown.Both);
        connectedPlayers[clientIndex].tcpSocket.Close(); // Closes TCP socket of client
        connectedPlayers[clientIndex].cancellationTokenSource.Cancel(); // Cancels receiving task from client
        connectedPlayers[clientIndex] = null; // Remove the player
    }
    async Task ConnectClient(Socket clientTcpSocket, IPEndPoint clientAddress, AuthenticationResult authenticationResult)
    {
        //bool[] indexArray = new bool[maxPlayers];
        //for (byte p = 0; p < maxPlayers; p++)
        //{
        //    if (connectedPlayers[p] == null)
        //    {
        //        indexArray[p] = false;
        //    }
        //    else
        //    {
        //        indexArray[p] = true;
        //    }
        //}

        for (byte index = 0; index < maxPlayers; index++)
        {
            if (connectedPlayers[index] == null)
            {
                //clientSlotTaken[index] = true; // New client will take found the empty slot 

                connectedPlayers[index] = new ConnectedPlayer
                {
                    index = index,
                    databaseID = authenticationResult.dbIndex,
                    tcpSocket = clientTcpSocket,
                    tcpEndpoint = clientAddress,
                    ipAddress = clientAddress.Address,
                    tcpPort = clientAddress.Port,
                    cancellationTokenSource = new CancellationTokenSource()
                };
                connectedPlayers[index].playerName = authenticationResult.playerName;

                InitialData initialData = new InitialData
                {
                    lr = authenticationResult.result, // value represents how the server responded to login, like if success or not
                    i = index, // client's assigned id
                    mp = maxPlayers // max player amount
                };
                Console.WriteLine(initialData.lr);

                string jsonData = JsonSerializer.Serialize(initialData, InitialDataContext.Default.InitialData);
                await SendTcp(1, jsonData, clientTcpSocket); // Type 1 means servers sends initial data to the new client
                _ = ReceiveTcpData(connectedPlayers[index]);
                break;
            }
        }

    }
    async Task ProcessBuffer(byte[] buffer, int byteLength, Socket clientTcpSocket, ConnectedPlayer connectedPlayer)
    {
        // try
        //{
        receivedBytesPerSecond += byteLength;
        string receivedBytesInString = string.Empty;
        if (encryption)
        {
            byte[] receivedBytes = new byte[byteLength];
            Array.Copy(buffer, receivedBytes, byteLength);

            receivedBytesInString = Encryption.Decrypt(receivedBytes, encryptionKey);
        }
        else
        {
            receivedBytesInString = Encoding.ASCII.GetString(buffer, 0, byteLength);
        }


        string packetTypePattern = @"#(.*)#";
        string packetDataPattern = @"\$(.*?)\$";

        MatchCollection packetTypeMatches = Regex.Matches(receivedBytesInString, packetTypePattern);
        MatchCollection packetDataMatches = Regex.Matches(receivedBytesInString, packetDataPattern);

        for (byte i = 0; i < packetTypeMatches.Count; i++)
        {
            byte.TryParse(packetTypeMatches[i].Groups[1].Value, out byte typeOfPacket);

            Packet packet = new Packet();
            packet.type = typeOfPacket;
            packet.data = packetDataMatches[i].Groups[1].Value;

            if (packet.type == 1)
                await ProcessDataSentByNewPlayer(packet, clientTcpSocket);
            else
                ProcessDataSentByPlayer(packet, connectedPlayer);
        }
        //}
        // catch
        // {
        //     System.Console.WriteLine("Error processing buffer");
        // }

    }
    void ProcessDataSentByPlayer(Packet packet, ConnectedPlayer conectedClient)
    {
        // try
        // {
        switch (packet.type)
        {
            // Type 0 means client answers the ping
            case 0:
                //conectedClient.pingAnswered = true;
                conectedClient.status = 1;
                CalculatePlayerLatency(conectedClient.index);
                break;
            // Type 3 means client is sending its own position to the server
            case 3:
                PlayerPosition clientPlayerPosition = JsonSerializer.Deserialize(packet.data, PlayerPositionContext.Default.PlayerPosition);
                conectedClient.position = clientPlayerPosition;
                break;
        }
        // }
        // catch
        // {
        //     Console.WriteLine("Packet error");
        // }
    }
    async Task ProcessDataSentByNewPlayer(Packet packet, Socket clientTcpSocket)
    {
        IPEndPoint clientAddress = (IPEndPoint)clientTcpSocket.RemoteEndPoint; // Gets the IP address of the new client

        LoginData loginData = JsonSerializer.Deserialize(packet.data, LoginDataContext.Default.LoginData);

        bool loginOrRegister = loginData.loginOrRegister; // True if client wants to login, false if client wants to register register
        string username = loginData.username;
        string hashedPassword = loginData.password;

        AuthenticationResult authenticationResult = new AuthenticationResult();
        if (loginOrRegister == false) // Runs if client wants to register
        {
            authenticationResult = Database.RegisterUser(username, hashedPassword);
            if (authenticationResult.result == 1) // runs if registration was successful
            {
                authenticationResult = Database.LoginUser(username, hashedPassword, connectedPlayers);
            }
        }
        else if (loginOrRegister == true) // Runs if client wants to login
        {
            authenticationResult = Database.LoginUser(username, hashedPassword, connectedPlayers);
        }


        if (authenticationResult.result == 1)
        {
            await ConnectClient(clientTcpSocket, clientAddress, authenticationResult);
        }
        else // login failed
        {
            InitialData initialData = new InitialData
            {
                lr = authenticationResult.result, // value represents how the server responded to login, like if success or not
                i = -1, // client's assigned id
                mp = -1 // max player amount
            };

            string jsonData = JsonSerializer.Serialize(initialData, InitialDataContext.Default.InitialData);
            await SendTcp(1, jsonData, clientTcpSocket); // Type 1 means servers sends initial data to the new client
            //Thread.Sleep(500); // workaround else client cant get the login failed response
            //clientTcpSocket.GetStream().Close();
        }
    }
    async Task SendTcp(byte commandType, string message, Socket clientTcpSocket)
    {
        try
        {
            byte[] messageBytes = EncodeMessage(commandType, message);
            CalculateSentBytes(messageBytes.Length);
            await clientTcpSocket.SendAsync(messageBytes, SocketFlags.None);

        }
        catch
        {
            //Console.WriteLine($"Error sending message type {commandType}.");
        }
        //catch
        //{
        //    Console.WriteLine($"Player is timing out");
        //}
    }
    async Task SendUdp(byte commandType, string message, EndPoint udpEndpoint)
    {
        try
        {
            //Console.WriteLine(message)
            byte[] messageBytes = EncodeMessage(commandType, message);
            CalculateSentBytes(messageBytes.Length);
            await serverUdpSocket.SendToAsync(messageBytes, SocketFlags.None, udpEndpoint);
        }
        catch
        {

        }
    }
    void CalculateSentBytes(int byteLength)
    {
        sentBytesPerSecond += byteLength;
    }
    byte[] EncodeMessage(byte commandType, string message)
    {
        if (encryption)
        {
            return Encryption.Encrypt($"#{commandType}#${message}$", encryptionKey);
        }
        else
        {
            return Encoding.ASCII.GetBytes($"#{commandType}#${message}$");
        }
    }
}