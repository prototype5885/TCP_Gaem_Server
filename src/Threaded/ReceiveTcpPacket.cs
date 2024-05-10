﻿using System.Net.Sockets;
using Gaem_server.Classes;
using Gaem_server.src.ClassesShared;
using Gaem_server.Static;
using log4net;

namespace Gaem_server.Threaded;

public class ReceiveTcpPacket(Server server, ConnectedPlayer connectedPlayer)
{
    private static readonly ILog logger = LogManager.GetLogger(typeof(ReceiveTcpPacket));

    private NetworkStream networkStream = connectedPlayer.tcpClient.GetStream();
    private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

    public async Task run()
    {
        try
        {
            byte[] buffer = new byte[4096];
            int bytesRead;
            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                byte[] receivedBytes = await ReceiveBytes(networkStream);
                logger.Debug($"Received message from {connectedPlayer.playerName}");
                List<Packet> packets = PacketProcessor.ProcessReceivedBytes(receivedBytes, connectedPlayer);

                foreach (Packet packet in packets)
                {
                    server.packetsToProcess.Enqueue(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.Debug("Receiving task was cancelled");
        }
        catch (Exception e)
        {
            logger.Error($"Error receiving TCP packet: {e}");
        }
        finally
        {
            server.DisconnectPlayer(connectedPlayer.tcpClient);
        }
    }

    public static async Task<byte[]> ReceiveBytes(NetworkStream networkStream)
    {
        byte[] buffer = new byte[4096];
        int bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length);
        byte[] receivedBytes = new byte[bytesRead];
        Array.Copy(buffer, receivedBytes, bytesRead);

        return receivedBytes;
    }
}

