﻿using System.Net.Sockets;
using System.Net;
using System.Text;

using System.Text.Json;
using System.Net.Http.Headers;

public class ServerUDP
{
    private int maxPlayers;

    private UdpClient udpClient;
    private IPEndPoint[] connectedUdpClient;

    private DataProcessing dataProcessing;

    private Database database = new Database(); // Creates database object
    private PacketProcessing packetProcessing;

    private bool[] clientPingedBack; // keeps track of which clients pinged or did not ping back

    public ServerUDP(int maxPlayers, DataProcessing dataProcessing, PacketProcessing packetProcessing)
    {
        this.maxPlayers = maxPlayers;
        this.dataProcessing = dataProcessing;
        this.packetProcessing = packetProcessing;

        StartUdpServer();
    }

    private void StartUdpServer()
    {
        try
        {
            const int port = 1943;
            udpClient = new UdpClient(port);

            connectedUdpClient = new IPEndPoint[maxPlayers];
            clientPingedBack = new bool[maxPlayers];

            Console.WriteLine($"UDP Server is listening on port {port}...");

            Task.Run(ReceiveDataUdp);
            Task.Run(SendDataUdp);
            Task.Run(PingClients);

        }
        catch (Exception ex)
        {
            Console.WriteLine("Server failed to start with exception: " + ex);
        }
    }

    private async Task ReceiveDataUdp()
    {
        while (true)
        {
            UdpReceiveResult udpReceiveResult = await udpClient.ReceiveAsync();

            Packet packet = packetProcessing.BreakUpPacket(udpReceiveResult.Buffer);

            try
            {
                switch (packet.type)
                {
                    case 0: // Client answers the ping
                        await Console.Out.WriteLineAsync($"A client from {udpReceiveResult.RemoteEndPoint} answered the ping");
                        int index = Array.IndexOf(connectedUdpClient, udpReceiveResult.RemoteEndPoint); // gets the index of client who pinged back

                        //clientPingedBack[index] = true;
                        break;
                    case 1: // If client wants to connect

                        await Console.Out.WriteLineAsync($"A client from {udpReceiveResult.RemoteEndPoint} is connecting");
                        byte[] messageByte = Encoding.ASCII.GetBytes("1"); // Reply 1 means that connection is accepted
                        await udpClient.SendAsync(messageByte, messageByte.Length, udpReceiveResult.RemoteEndPoint);
                        break;

                    case 2: // If client wants to login/register
                        await Console.Out.WriteLineAsync($"A client from {udpReceiveResult.RemoteEndPoint} is trying to login or register");
                        await Authentication(packet.data, udpReceiveResult.RemoteEndPoint);
                        break;

                    case 3: // If client sends its own position
                        Player clientPlayer = JsonSerializer.Deserialize(packet.data, PlayerContext.Default.Player);

                        foreach (IPEndPoint clientAddress in connectedUdpClient)
                        {
                            if (clientAddress != null)
                            {
                                int clientIndex = Array.IndexOf(connectedUdpClient, clientAddress);
                                if (udpReceiveResult.RemoteEndPoint.Equals(connectedUdpClient[clientIndex]))
                                {
                                    dataProcessing.ProcessPositionOfClients(clientIndex, clientPlayer);
                                }
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
    private async Task SendDataUdp()
    {
        while (true)
        {
            foreach (IPEndPoint clientAddress in connectedUdpClient)
            {
                if (clientAddress != null)
                {
                    int index = Array.IndexOf(connectedUdpClient, clientAddress);
                    try
                    {
                        string jsonData = JsonSerializer.Serialize(dataProcessing.players, PlayersContext.Default.Players);
                        int commandType = 4; // Type 4 means server is sending position of other players
                        byte[] messageByte = Encoding.ASCII.GetBytes($"#{commandType}#{jsonData}");
                        await udpClient.SendAsync(messageByte, messageByte.Length, clientAddress);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error sending UDP message to client index {index}. Exception: {ex.Message}");
                    }
                }
            }
            //dataProcessing.PrintConnectedClients();

            Thread.Sleep(10);
        }
    }
    private async Task PingClients()
    {
        IPEndPoint clientAddress;
        while (true)
        {
            for (int index = 0; index < maxPlayers; index++)
            {
                clientAddress = connectedUdpClient[index];
                if (clientAddress != null)
                {
                    //Console.WriteLine(clientAddress);

                    //if (clientPingedBack[index] == false)
                    //{
                    //    //Console.WriteLine("index: " + index);
                    //    //Console.WriteLine("length: " + connectedUdpClient.Length);
                    //    //connectedUdpClient[index].Addr;
                    //    Console.WriteLine($"Client {connectedUdpClient[index]} did not answer");
                    //    connectedUdpClient[index] = null;
                    //}
                    //clientPingedBack[index] = false; // resets the array

                    int commandType = 0; // Type 0 means pinging
                    byte[] messageByte = Encoding.ASCII.GetBytes($"#{commandType}#");
                    await udpClient.SendAsync(messageByte, messageByte.Length, clientAddress);

                }
            }

            Thread.Sleep(1000);
        }
    }
    private async Task Authentication(string packetString, IPEndPoint clientAddress) // Authenticate the client
    {
        Console.WriteLine("Starting authentication of client...");


        // Converts received json format data to username and password
        LoginData loginData = new LoginData();
        try
        {
            loginData = JsonSerializer.Deserialize(packetString, LoginDataContext.Default.LoginData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex.Message}");
        }

        bool LoginOrRegister = loginData.lr; // True if client wants to login, false if client wants to register register
        string username = loginData.un;
        string hashedPassword = loginData.pw;

        if (LoginOrRegister == true) // Runs if client wants to login
        {
            if (database.LoginUser(username, hashedPassword)) // Checks if username and password exists in the database
            {
                Console.WriteLine("Login of client successful");

                // Send a response back to the client
                string response = "1"; // Response 1 means the username/password were accepted
                byte[] messageByte = Encoding.ASCII.GetBytes(response); // Adds the length of the message
                await udpClient.SendAsync(messageByte, messageByte.Length, clientAddress);

                // Accepts connection
                CheckForFreeSlots(clientAddress, username);
            }
            else // Rejects
            {
                Console.WriteLine("Client entered wrong username or password");

                // Send a response back to the client
                string response = "2"; // Response 2 means the username/password were rejected
                byte[] messageByte = Encoding.ASCII.GetBytes(response); // Adds the length of the message
                await udpClient.SendAsync(messageByte, messageByte.Length, clientAddress);

                // Rejects connection
                //client.Close();
            }
        }
        else // Runs if client wants to register
        {
            if (username.Length > 16) // Checks if username is longer than 16 characters
            {
                Console.WriteLine("Client's chosen username is too long");
                string response = "2"; // Response 2 means username is too long
                byte[] messageByte = Encoding.ASCII.GetBytes(response); // Adds the length of the message
                await udpClient.SendAsync(messageByte, messageByte.Length, clientAddress);
            }
            else if (database.RegisterUser(username, hashedPassword, clientAddress.Address.ToString())) // Runs if registration was succesful
            {
                Console.WriteLine("Registration of client was successful");
                string response = "1"; // Response 1 means registration was successful
                byte[] messageByte = Encoding.ASCII.GetBytes(response); // Adds the length of the message
                await udpClient.SendAsync(messageByte, messageByte.Length, clientAddress);

                CheckForFreeSlots(clientAddress, username);
            }
            else
            {
                Console.WriteLine("Client's chosen username is already taken");
                string response = "3"; // Response 3 means username is already taken
                byte[] messageByte = Encoding.ASCII.GetBytes(response); // Adds the length of the message
                                                                        //await authenticationStream.WriteAsync(messageByte, 0, messageByte.Length);
                await udpClient.SendAsync(messageByte, messageByte.Length, clientAddress);
            }
        }
    }
    void CheckForFreeSlots(IPEndPoint clientAddress, string username)
    {
        string clientIpAddress = (clientAddress).Address.ToString(); // Gets the IP address of the accepted

        database.UpdateLastLoginIP(username, clientIpAddress);

        int index = dataProcessing.FindSlotForClientUdp(connectedUdpClient, clientAddress); // Find an available slot for the new client

        if (index != -1) // Runs if there are free slots
        {
            ClientAccepted(clientAddress, index);
        }
        else // Reject the connection if all slots are occupied
        {

        }
    }
    void ClientAccepted(IPEndPoint clientAddress, int index)
    {
        connectedUdpClient[index] = clientAddress;
        dataProcessing.AddNewClientToPlayersList(index); // Assings new client to list managing player position

        SendInitialData(clientAddress, index);
    }
    void SendInitialData(IPEndPoint clientAddress, int index) // Sends the client the initial stuff
    {
        try
        {
            //NetworkStream sendingStream = client.GetStream();

            InitialData initialData = new InitialData();
            initialData.i = index; // Prepares sending client's own index to new client
            initialData.mp = maxPlayers; // Prepares sending maxplayers amount to new client

            string jsonData = JsonSerializer.Serialize(initialData, InitialDataContext.Default.InitialData);

            byte[] messageByte = Encoding.ASCII.GetBytes(jsonData);
            udpClient.Send(messageByte, messageByte.Length, clientAddress);


        }
        catch (Exception ex)
        {
            Console.Out.WriteLine(ex.ToString());
        }
    }

}

