using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;


namespace Robot
{
    class Program
    {
        const int port = 8888;
        static void Main(string[] args)
        {
            IPEndPoint ipPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), port);
            Socket listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            
            try
            {
                listenSocket.Bind(ipPoint);
                listenSocket.Listen(10);

                while(true)
                {
                    Socket handler = listenSocket.Accept();
                    Robot clientObject = new Robot(handler);
                    
                    Thread clientThread = new Thread(new ThreadStart(clientObject.process));
                    clientThread.Start();
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                listenSocket.Shutdown(SocketShutdown.Both);
                listenSocket.Close();
            }
        }
    }


public class Robot
    {
        public enum Direction { UP, RIGHT, DOWN, LEFT };

        public static readonly int[] serverKey = {23019, 32037, 18789, 16443, 18189};
        public static readonly int[] clientKey = {32037, 29295, 13603, 29533, 21952};
        
        const int TIMEOUT = 1;
        const int TIMEOUT_RECHARGING = 5;

        public Socket handler;
        
        public (int x, int y) pos;
        public Direction dir;
        
        public Queue<string> responses = new Queue<string>();
        public Robot(Socket handler)
        {
            this.handler = handler;
        }
        bool auth()
        {
            string authData;
            while(responses.Count == 0) if(!readResponse(20)) return false; // checking client's hash 
            authData = responses.Dequeue();
            if (authData == "err" || authData.Length >= 18) // checking max length of username
                return sendErrorMessage("301 SYNTAX ERROR\a\b");
            byte[] authDataBy = ASCIIEncoding.ASCII.GetBytes(authData);
            int hash = 0;
            foreach (byte b in authDataBy) hash += b;
            
            sendMessage("107 KEY REQUEST\a\b");
            while (responses.Count == 0) if(!readResponse(5)) return false;
            
            authData = responses.Dequeue();
            Console.WriteLine("Data: {0}", authData);
            if (authData == "err" || !Regex.IsMatch(authData, @"^\d+$")) // checking if keyID is a number
                return sendErrorMessage("301 SYNTAX ERROR\a\b");
            int keyID = Int32.Parse(authData);
            Console.WriteLine("Debug: keyID = {0}", keyID);
            if (keyID >= 5 || keyID < 0)
                return sendErrorMessage("303 KEY OUT OF RANGE\a\b");
            
            sendMessage(((((hash * 1000) % 65536) + serverKey[keyID]) % 65536).ToString() + "\a\b");
            hash = (((hash * 1000) % 65536) + clientKey[keyID]) % 65536;
            while (responses.Count == 0) if(!readResponse(7)) return false; // read confirmation;
            authData = responses.Dequeue();
            if (authData == "err" || !Regex.IsMatch(authData, @"^\d+$") || authData.Contains(" ") || authData.Length > 5) // checking client's hash
                return sendErrorMessage("301 SYNTAX ERROR\a\b");
            
            int client_hash = 0;
            if (!int.TryParse(authData, out client_hash))
                return sendErrorMessage("300 LOGIN FAILED\a\b");
            
            if (client_hash == hash) sendMessage("200 OK\a\b");
            else return sendErrorMessage("300 LOGIN FAILED\a\b");
            return true;
        }

        void sendMessage(string message)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            Console.WriteLine("Send id={0}: {1}", Thread.CurrentThread.ManagedThreadId, message);
            this.handler.Send(Encoding.ASCII.GetBytes(message));
        }

        static int getActualTime()
        {
            DateTime dt = DateTime.Now;
            int ms = dt.Millisecond + dt.Second * 1000;
            return ms;
        }
        
        bool sendErrorMessage(string message)
        {
            sendMessage(message);
            return false;
        }
        
        bool readResponse(int MAX_LENGTH)
        {
            Byte[] data = new Byte[256];
            int oms = getActualTime();
            int ams = getActualTime();
            String responseData = String.Empty;
            bool flag = false;
            for (;;)
            {
                oms = ams;
                ams = getActualTime();
                bool no_data = false;
                if (responseData.Length >= MAX_LENGTH && responseData.Split("\a\b").Length < 2) return sendErrorMessage("301 SYNTAX ERROR\a\b");
                if ((responseData.Length < 2 || responseData[responseData.Length - 2] != '\a' || responseData[responseData.Length - 1] != '\b') && (ams - oms) > TIMEOUT * 1000)
                {
                    if (responseData.Length == 0) return sendErrorMessage("106 LOGOUT\a\b");
                    return sendErrorMessage("301 SYNTAX ERROR\a\b");
                }
                else if ((responseData.Length < 2 || responseData[responseData.Length - 2] != '\a' || responseData[responseData.Length - 1] != '\b'))
                {
                    if(!handler.Connected) return sendErrorMessage("106 LOGOUT\a\b");
                    Int32 bytes = handler.Receive(data);
                    string add = System.Text.Encoding.ASCII.GetString(data, 0, bytes);
                    
                    responseData += add;
                    if(responseData != "") Console.WriteLine("Received id={0}: {1}", Thread.CurrentThread.ManagedThreadId, responseData);
                    if (responseData.Contains("RECHARGING"))
                    {
                        Console.WriteLine("Recharging...");
                        handler.ReceiveTimeout = TIMEOUT_RECHARGING * 1000;
                        for (;;)
                        {
                            if (!handler.Connected) return sendErrorMessage("106 LOGOUT\a\b");
                            Int32 statusBytes = handler.Receive(data);
                            string status = System.Text.Encoding.ASCII.GetString(data, 0, statusBytes);
                            if (status.Contains("FULL POWER\a\b"))
                            {
                                Console.WriteLine("byl");
                                string[] statusSPL = status.Split("\a\b");
                                foreach (var stat in statusSPL)
                                {
                                    if (stat != "" && stat != "FULL POWER")
                                    {
                                        responses.Enqueue(stat);
                                        flag = true;
                                    }
                                }
                                break;
                            }
                            if (status != "")
                            {
                                Console.WriteLine("d: {0}", status);
                                return sendErrorMessage("302 LOGIC ERROR\a\b");
                            }
                        }
                        handler.ReceiveTimeout = TIMEOUT * 1000;
                        if (flag) break;
                    }
                }
                else
                {
                    responseData = responseData.Remove(responseData.Length - 2, 2);
                    string[] responsesSplited = responseData.Split("\a\b");
                    foreach (var response in responsesSplited)
                    {
                        if(response != "RECHARGING") responses.Enqueue(response);
                    }
                    break;
                }
            }
            return true;
        }

        

        bool sendControlCommand(string command)
        {
            string parse;
            sendMessage(command);
            while(responses.Count == 0) if(!readResponse(12)) return false;
            parse = responses.Dequeue();
            var res = parse.Split(' ');
            int n;
            if (res[0] != "OK" || !int.TryParse(res[1], out n) || !int.TryParse(res[2], out n) || res.Length > 3)
            {
                sendMessage("301 SYNTAX ERROR\a\b");
                return false;
            }
            pos.x = Int32.Parse(res[1]);
            pos.y = Int32.Parse(res[2]);
            return true;
        }

        bool tryGetMessage()
        {
            string message;
            sendMessage("105 GET MESSAGE\a\b");
            while (responses.Count == 0) if(!readResponse(100)) return false;
            message = responses.Dequeue();
            if (message == "") return false;
            Console.WriteLine("Secret message: {0}", message);
            sendMessage("106 LOGOUT\a\b");
            return true;
        }

        bool moveTo(Direction to_direction)
        {
            for (;;)
            {
                if (dir == to_direction) break;
                if (!sendControlCommand("104 TURN RIGHT\a\b")) return false;
                dir = (Direction) (((int) dir + 1) % 4);
            }
            if (!sendControlCommand("102 MOVE\a\b")) return false;
            return true;
        }

        bool getPositionDirection()
        {
            (int x, int y) t_pos;
            dir = Direction.UP;
            if (!sendControlCommand("102 MOVE\a\b")) return false;
            t_pos = pos;
            if (!sendControlCommand("102 MOVE\a\b")) return false;
            if      (pos.x - t_pos.x == 0 && pos.y - t_pos.y == 0)
            {
                if (!sendControlCommand("104 TURN RIGHT\a\b")) return false;
                if (!getPositionDirection()) return false;
            }
            else if (pos.y - t_pos.y == 1 ) dir = Direction.UP;
            else if (pos.y - t_pos.y == -1) dir = Direction.DOWN;
            else if (pos.x - t_pos.x == 1 ) dir = Direction.RIGHT;
            else if (pos.x - t_pos.x == -1) dir = Direction.LEFT;
            return true;
        }
        
        bool robotLogic()
        {
            if (!getPositionDirection()) return false;
            (int x, int y) old_pos;
            for (;;)
            {
                old_pos = pos;
                if (pos.x == 0 && pos.y == 0) break;
                else if (pos.x < 0)
                {
                    if (!moveTo(Direction.RIGHT)) return false;
                    if (pos == old_pos)
                    {
                        if (!moveTo(Direction.DOWN)) return false;
                        if (!moveTo(Direction.RIGHT)) return false;
                    }
                }
                else if (pos.x > 0)
                {
                    if (!moveTo(Direction.LEFT)) return false;
                    if (pos == old_pos)
                    {
                        if (!moveTo(Direction.UP)) return false;
                        if (!moveTo(Direction.LEFT)) return false;
                    }
                }
                if (pos.y < 0)
                {
                    if (!moveTo(Direction.UP)) return false;
                    if (pos == old_pos)
                    {
                        if (!moveTo(Direction.RIGHT)) return false;
                        if (!moveTo(Direction.UP)) return false;
                    }
                }
                else if (pos.y > 0)
                {
                    if (!moveTo(Direction.DOWN)) return false;
                    if (pos == old_pos)
                    {
                        if (!moveTo(Direction.LEFT)) return false;
                        if (!moveTo(Direction.DOWN)) return false;
                    }
                }                    
            }
            
            if (tryGetMessage()) return true;
            return false;
        }

        public void process()
        {
            handler.ReceiveTimeout = TIMEOUT * 1000;
            try
            {
                bool flag = false;

                if (auth()) robotLogic();
                handler.Shutdown(SocketShutdown.Both);
                handler.Close();
            }
            catch (Exception e)
            {
                handler.Shutdown(SocketShutdown.Both);
                handler.Close();
                Console.WriteLine(e.Message);
            }
        }
    }
}