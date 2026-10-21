
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class BrickTcpSender : MonoBehaviour
{
    public string host = "127.0.0.1";
    public int port = 5000;
    public float sendInterval = 0.1f;

    TcpClient client;
    StreamWriter writer;
    float nextSend;

    void Update()
    {
        if (Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + sendInterval;

        try
        {
            if (client == null || !client.Connected)
                Connect();

            var Bricks = GameObject.FindGameObjectsWithTag("Brick");

            string message = string.Join(";", Bricks.Select(go =>
            {
                Vector3 p = go.transform.position;
                Vector3 right = go.transform.right;
                Vector3 forward = go.transform.forward;

                return string.Join(",", new[]
                {
                    go.GetInstanceID().ToString(CultureInfo.InvariantCulture),

                    p.x.ToString(CultureInfo.InvariantCulture),
                    p.y.ToString(CultureInfo.InvariantCulture),
                    p.z.ToString(CultureInfo.InvariantCulture),

                    right.x.ToString(CultureInfo.InvariantCulture),
                    right.y.ToString(CultureInfo.InvariantCulture),
                    right.z.ToString(CultureInfo.InvariantCulture),

                    forward.x.ToString(CultureInfo.InvariantCulture),
                    forward.y.ToString(CultureInfo.InvariantCulture),
                    forward.z.ToString(CultureInfo.InvariantCulture)
                });
            }));

            writer.WriteLine(message);
        }
        catch (Exception e)
        {
            Debug.LogWarning("TCP disconnected: " + e.Message);
            CloseConnection();
        }
    }

    void Connect()
    {
        client = new TcpClient();
        client.Connect(host, port);

        writer = new StreamWriter(
            client.GetStream(), new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        Debug.Log("Connected to Grasshopper TCP receiver");
    }

    void CloseConnection()
    {
        writer?.Dispose();
        client?.Close();
        writer = null;
        client = null;
    }

    void OnDestroy()
    {
        CloseConnection();
    }
}
