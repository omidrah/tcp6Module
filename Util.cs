﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using TCPServer.Models;
using System.Collections.Generic;
using System.Net.Sockets;

namespace TCPServer
{
    public static class Util
    {
        public static string serverPath { get; set; }
        public static string ConnectionStrings { get; set; }
        public readonly static CultureInfo EnglishCulture = new CultureInfo("en-US");
        internal static async Task ProcessProbeRecievedContent(StateObject state, string content)
        {
            try
            {
                string text = (content ?? ",").ToString().Split(" ,")[1];
                string vikey = (content ?? ",").ToString().Split(" ,")[0];
                string body = text.Decrypt("sample_shared_secret", vikey);
                _ = Util.LogErrorAsync(new Exception("Socekt ReadCallback"), (body ?? "").ToString(), state.IP);
                string[] paramArray = body.Replace("\\", string.Empty).Replace("\"", string.Empty).Split('#');
                switch (paramArray[0])
                {
                    case "MID":  //for register device and first connection to server --omid added
                         ProcessMIDRecievedContent(state, paramArray).ConfigureAwait(false);
                        break;
                    case "MID6":  //for register device and first connection to server --omid added
                        ProcessMIDRecievedContent(state, paramArray).ConfigureAwait(false);
                        break;
                    case "TSC": //for communication and run task on device between device and server --omid added
                        ProcessTSCRecievedContent(state, paramArray).ConfigureAwait(false);
                        break;
                    case "UPG": //Device Say that , its get Update message 
                        _= ProcessUPGRecievedContent(state, paramArray).ConfigureAwait(false);
                        break;
                    case "UPR": //Device Say that , Download newUpdate file finished
                        _= ProcessUPRRecievedContent(state, paramArray).ConfigureAwait(false);
                        break;
                    case "RPL": //Device Send this message and mean download newUpdate file and install Done Successful
                        _= ProcessRPLRecievedContent(state, paramArray).ConfigureAwait(false);
                        break;
                    case "LST": //for show low battery device ... --omid added // در واقغ وسیله وقتی شارژش از یک مقداری کمتر شد با این پیام به سرور اطلاع داده و قطع میشود
                        ProcessLowBatteryDevice(state, paramArray);
                        break;
                    case "LOC": //for show Gps Device every 50 second ... --omid added 990220//هر پنجاه ثانیه یکبار جی پی اس از دستگاه دریافت میشود//addby omid
                        _= ProcessLoCDevice(state, paramArray);
                        break;
                    case "FLD": //اگر زمان پایان یا شروغ تست درست تعریف نشده باشد این پیام دریافت میگردد که باید تست را تمام شده در نظرگرفت//addby omid
                        _ = ProcessFLDDevice(state, paramArray);
                        break;
                    case "SFS": //فایل لاگ ارسالی برای عمل همسان سازی توسط دستگاه از طریق این پیام دریافت  میگردد//addby omid
                                //format msg from clietn : SFS#SId#IM1#TIME
                        _ = ProcessSFSDevice(state, paramArray).ConfigureAwait(false);
                        break;
                    case "SYE": //log file Upload Successfully, end Sync client by Server.Now Server Start Update Tables.
                               //format msg from client : SYE#SId#IM1#TIME
                        _ = ProcessSYEDevice(state, paramArray).ConfigureAwait(false);
                        break;
                    case "HDS": //addby omid  -- for handshake-- YesI'mThere
                        _ = CheckHandShake(state, paramArray).ConfigureAwait(false);
                        break;
                    default:
                        break;
                }
            }
            catch (Exception e)
            {
                 _=Util.LogErrorAsync(e, $"ProcessProbeRecievedContent >> Decrypt/Replace - @ {DateTime.Now}");
            }
         }
        private static async Task ProcessSYEDevice(StateObject state, string[] paramArray)
        {
                _ = LogErrorAsync(new Exception($"SYE of Sync Process on IMEI1 = {state.IMEI1},IMEI2={state.IMEI2}"), $"IMEI ={state.IMEI1},IMEI2={state.IMEI2} IP= {state.IP}").ConfigureAwait(false);
                int syncMasterId; int.TryParse(paramArray[1].Split(':')[1], out syncMasterId);
                bool res=false;
                await UpdateMasterDetailSync(state, syncMasterId, "SYE", true);
          
        }
        private static async Task CheckHandShake(StateObject item, string[] paramArray)
        {  
            item.IsConnected = true;
            item.lastDateTimeConnected = DateTime.Now;
            item.counter = 0;
            int slaveCnt;
            int.TryParse(paramArray[2].Split(':')[1],out slaveCnt);
            if(slaveCnt > 0)
            {
                try
                {
                     UpdateMachineAfterHandshake(item.workSocket,item.IMEI1, paramArray, "HDS").ConfigureAwait(false);
                }
                catch(Exception ex)
                {
                    throw ex;
                }               
            }
        }
        private static async Task SyncTest(StateObject item, string[] paramArray, int slaveCnt)
        {
            for (int i = 3; i < 14; i += 2) //به ازای همه فرزندان مستر عمل بروزرسانی انجام میگیرد
            {
                var IMEI2 = paramArray[i].Split(':')[1].Trim();
                var Imei2_state = paramArray[i + 1].Split(':')[1].Trim() == "0" ? false : true;
                if (Imei2_state)
                {
                    item.IMEI2 = IMEI2;
                    _ = Util.SyncDevice(item);
                }
            }
        }

       
        /// <summary>
        /// Status Of Slaves of Master(Imei1) Updated
        /// </summary>
        /// <param name="Imei1">Master IMEI1</param>
        /// <param name="paramArray">HDS Message </param>        
        /// <param name="callfrom">this Method Call from ..</param>
        /// <returns></returns>
        private static async Task UpdateMachineAfterHandshake(Socket MasterSocket, string Imei1,
                 string[] paramArray,string callfrom)
        {
            var slaves =await fillSlave(paramArray);
            for (int i = 0; i < slaves.Count; i++)
            {
                var str = $"IMEI: {slaves.ElementAt(i).Key}, State: {slaves.ElementAt(i).Value}";
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"*************************************");
                Console.WriteLine($" {str} ---  @{DateTime.Now}         *");
                Console.WriteLine($"*************************************");
                Console.ForegroundColor = ConsoleColor.Green;
                _ = LogErrorAsync(new Exception("UpdateMachineAfterHandShake"), str, callfrom).ConfigureAwait(false);
                _ = UpdateMachinesState(
                          Imei1,
                          slaves.ElementAt(i).Key,  //Imei
                          slaves.ElementAt(i).Value, //status
                          "0", "0", "UpdateMachineAfterHandshake").ConfigureAwait(false);
               _ = SendWaitingTest(MasterSocket, Imei1, slaves.ElementAt(i).Key).ConfigureAwait(false);                
            }
        }
        /// <summary>
        /// List of Slaves and Status 
        /// </summary>
        /// <param name="paramArray">Slaves Identify From HDS Message</param>
        /// <returns>Dictionary of salves and states</returns>
        private async static Task<Dictionary<string,bool>> fillSlave(string[] paramArray)
        {
            Dictionary<string, bool> devByState = new Dictionary<string, bool>();
            for (int i = 3; i < 14; i += 2) //به ازای همه فرزندان مستر بهمراه وضعیتشان
            {
                //check socket for imei1                      
                var IMEI2 = paramArray[i].Split(':')[1].Trim();
                var Imei2_state = paramArray[i + 1].Split(':')[1].Trim() == "0" ? false : true;
                devByState.Add(IMEI2, Imei2_state);
            }
            return devByState;
        }
        private async static Task ProcessSFSDevice(StateObject state, string[] paramArray)
        {
            int syncMasterId;int.TryParse(paramArray[1].Split(':')[1],out syncMasterId);               
            await UpdateMasterDetailSync(state,syncMasterId, "SFS").ConfigureAwait(false);           
        }
        private async static Task SyncDevice(StateObject state)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"*************************************");
            Console.WriteLine($"Sync Time IMEI1 ={state.IMEI1},IMEI2={state.IMEI2} IP={state.IP}");
            Console.WriteLine($"*************************************");
            Console.ForegroundColor = ConsoleColor.Green;                       
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                try
                {
                    string sql = $"select top 1 * from machineConnectionHistory where machineId = " +
                                 $"              (select Id from machine where imei1=@Im1 and imei2=@Im2) " +
                                 $" and IsConnected=0 order by CreatedDate desc";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        command.Parameters.AddWithValue("@Im1", state.IMEI1);
                        command.Parameters.AddWithValue("@Im2", state.IMEI2);
                        connection.Open();
                        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                        {                            
                            if (await reader.ReadAsync())
                            {
                                if (reader.HasRows)
                                {
                                    DateTime disTime = DateTime.Parse(reader["CreatedDate"].ToString());
                                    int machineId = int.Parse(reader["MachineId"].ToString());
                                     AddSync(state, disTime, machineId);
                                }
                            }
                        }
                        
                    }
                }
                catch (Exception ex)
                {
                     _=LogErrorAsync(ex, "100 --Method-- ProcessFLDDevice-StopSendTest").ConfigureAwait(false);
                }
                finally
                {
                    connection.Close();
                }
            }
        }
        private async static Task<bool> UpdateMasterDetailSync(StateObject state,int syncMasterId,string step,bool? iscompeleted=null)
        {
           bool result = false;
           using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                connection.Open();
                var tx = connection.BeginTransaction();
                try
                {
                    var sql = $"insert into SyncDetail " +
                        $"(PsyncId,CreateDate,Command,status) values" +
                        $" (@PsyncId,@CreateDate,@Command,@status)";
                    using (SqlCommand command2 = new SqlCommand(sql, connection))
                    {
                        command2.CommandTimeout = 100000;
                        command2.CommandType = CommandType.Text;
                        command2.Transaction = tx;
                        command2.Parameters.AddWithValue("@PsyncId", syncMasterId);
                        command2.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                        command2.Parameters.AddWithValue("@Command", step);
                        command2.Parameters.AddWithValue("@status", 1);
                        await command2.ExecuteScalarAsync().ConfigureAwait(false);
                        
                        if (iscompeleted != null)
                        {
                            sql = "update SyncMaster set Status=@Status , IsCompeleted = @IsCompeleted  " +
                                  " where Id = @syncMasterid  ";
                        }
                        else
                        {
                            sql = "update SyncMaster set Status=@Status  where Id = @syncMasterid  ";
                        }                                           
                        using (SqlCommand command = new SqlCommand(sql, connection))
                        {
                            command.CommandTimeout = 100000;
                            command.CommandType = CommandType.Text;
                            command.Transaction = tx;
                            command.Parameters.AddWithValue("@Status", 2);
                            command.Parameters.AddWithValue("@syncMasterid", syncMasterId);
                            if (iscompeleted != null)
                            {
                                command.Parameters.AddWithValue("@IsCompeleted", iscompeleted);
                            }
                            await command.ExecuteScalarAsync().ConfigureAwait(false);
                            tx.Commit();
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine($"*************************************");
                            if (iscompeleted != null)
                            {
                                Console.WriteLine($"Successfully Upload Sync File from IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP={state.IP}");
                                _ = LogErrorAsync(new Exception($"Successfully Upload {step} of Sync Process on IMEI1={state.IMEI1},IMEI2={state.IMEI2}"), $"IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP= {state.IP}").ConfigureAwait(false);
                            }
                            else
                            {
                                Console.WriteLine($"Success: {step} of Sync Process on IMEI1 ={state.IMEI1} ,IMEI2 ={state.IMEI2} IP={state.IP}");
                                _ = LogErrorAsync(new Exception($"{step} of Sync Process on IMEI1={state.IMEI1},IMEI2={state.IMEI2}"), $"IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP= {state.IP}").ConfigureAwait(false);
                            }                            
                            Console.WriteLine($"*************************************");
                            Console.ForegroundColor = ConsoleColor.Green;                            
                            result = true;
                        }                        
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"*************************************");
                    Console.WriteLine($"Failed: {step} of Sync Process on IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP={state.IP}");
                    Console.WriteLine($"*************************************");
                    Console.ForegroundColor = ConsoleColor.Green;
                    _ = LogErrorAsync(ex, $"Failed { step} of Sync Process on IMEI1={ state.IMEI1},IMEI2= {state.IMEI2}", $"IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP= {state.IP}").ConfigureAwait(false);
                    tx.Rollback();
                    result = false;
                }
                finally
                {
                    connection.Close();
                }
            }
            return result;
        }
        private async static Task AddSync(StateObject state,DateTime disTime,int machineId)
        {
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                connection.Open();
                var tx = connection.BeginTransaction();
                try
                {
                   string sql = "insert into SyncMaster (MachineId,IMEI1,IMEI2,Status,CreateDate,DisconnectedDate,CntFileGet,IsCompeleted) " +
                   " values (@MachineId,@IMEI1,@IMEI2,@Status,@CreateDate,@DisconnectedDate,@CntFileGet,@IsCompeleted);select SCOPE_IDENTITY()";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        command.Transaction = tx;                                                
                        command.Parameters.AddWithValue("@MachineId", machineId);
                        command.Parameters.AddWithValue("@IMEI1", state.IMEI1);
                        command.Parameters.AddWithValue("@IMEI2", state.IMEI2);
                        command.Parameters.AddWithValue("@Status", 1);
                        command.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                        command.Parameters.AddWithValue("@DisconnectedDate", disTime);
                        command.Parameters.AddWithValue("@CntFileGet", 0);
                        command.Parameters.AddWithValue("@IsCompeleted", 0);
                        var syncMasterId =  (decimal)await command.ExecuteScalarAsync().ConfigureAwait(false);
                        sql = $"insert into SyncDetail (PsyncId,CreateDate,Command,status) values (@PsyncId,@CreateDate,@Command,@status)";
                        using (SqlCommand command2 = new SqlCommand(sql, connection))
                        {   
                            command2.CommandTimeout = 100000;
                            command2.CommandType = CommandType.Text;
                            command2.Transaction = tx;
                            command2.Parameters.AddWithValue("@PsyncId", syncMasterId);
                            command2.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                            command2.Parameters.AddWithValue("@Command", "SYN");
                            command2.Parameters.AddWithValue("@status", 1);
                            await command2.ExecuteScalarAsync().ConfigureAwait(false);
                            sql = $"select * from machine where Id =@Id";
                            using (SqlCommand command3 = new SqlCommand(sql, connection))
                            {
                                command3.CommandTimeout = 100000;
                                command3.CommandType = CommandType.Text;
                                command3.Transaction = tx;
                                command3.Parameters.AddWithValue("@Id", syncMasterId);
                                string hostname="pool.ntp.org", timezone = "UTC"; //default value
                                using (var reader = await command3.ExecuteReaderAsync().ConfigureAwait(false))
                                {
                                    if (await reader.ReadAsync())
                                    {
                                         hostname = reader["HostName"].ToString();
                                         timezone = reader["TimeZone"].ToString();                                        
                                    }
                                }
                                tx.Commit();
                                string VIKey = "BgrUEy5IbpJSnhmqI2IhKw==";
                                var newMsg = $"SYN#\"IMEI1\":\"{state.IMEI1}\"#\"IMEI2\":\"{state.IMEI2}\"#";
                                var msg = ($"{newMsg}\"SId\":{syncMasterId}#" +
                                    $"\"DisconnectTime\":\"{disTime.ToString("yyyy-M-dHH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)}\"#"+
                                    $"\"HostName\":\"{hostname}\"#\"TimeZone\":\"{timezone}\"");
                                var content = msg.Encrypt("sample_shared_secret", VIKey);

                                AsynchronousSocketListener.Send(state.workSocket, VIKey + " ," + content);
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"*************************************");
                                Console.WriteLine($"Sync Send from Server  {msg }");
                                Console.WriteLine($"\n");
                                Console.WriteLine($"Send Sync IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP={state.IP}");
                                Console.WriteLine($"*************************************");
                                Console.ForegroundColor = ConsoleColor.Green;
                                _ = LogErrorAsync(new Exception($"Send Sync IMEI1={state.IMEI1},IMEI2={state.IMEI2}"), msg, $"IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP= {state.IP}").ConfigureAwait(false);
                            }                            
                        }                        
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"*************************************");
                    Console.WriteLine($"Dont Send Sync IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP={state.IP}");
                    Console.WriteLine($"*************************************");
                    Console.ForegroundColor = ConsoleColor.Green;
                    _ = LogErrorAsync(ex, ex.Message, $"IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} IP= {state.IP}").ConfigureAwait(false);
                    tx.Rollback();
                }
                finally
                {
                    connection.Close();
                }
            }         
        }

        private async static Task ProcessFLDDevice(StateObject state, string[] paramArray)
        {
            if (paramArray.Length > 3)
            {
                if (!string.IsNullOrEmpty(paramArray[2].Split(':')[1]))
                {
                    var im1 = paramArray[1].Split(':')[1];
                    var im2 = paramArray[2].Split(':')[1];
                    int.TryParse(paramArray[4].Split(':')[1], out int definedTestMachineId);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"*************************************");
                    Console.WriteLine("        Time Error In Test            ");
                    Console.WriteLine($"Test by Id = {definedTestMachineId} for Machine by IMEI1={im1},IMEI2={im2} has Error in Date/Time");
                    Console.WriteLine($"*************************************");
                    Console.ForegroundColor = ConsoleColor.Green;
                     await StopSendTest(definedTestMachineId).ConfigureAwait(false);
                }
            }
        }
        /// <summary>
        /// تستی که بازه زمانی نامناسب دارد را
        /// دیگر برای دستگاه ارسال نمی کنیم
        /// </summary>
        /// <param name="definedTestMachineId">آی دی تست دریافتی</param>
        /// <returns></returns>
        private async static Task StopSendTest(int definedTestMachineId)
        {
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                try
                {
                    string sql = $"Update  definedTestMachine Set Status=1, FinishTime=@currentDate where Id =@Id";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        command.Parameters.AddWithValue("@Id", definedTestMachineId);
                        command.Parameters.AddWithValue("@currentDate", DateTime.Now);
                        connection.Open();
                        await command.ExecuteScalarAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _= LogErrorAsync(ex, "100 --Method-- ProcessFLDDevice-StopSendTest").ConfigureAwait(false);
                }
                finally
                {
                    connection.Close();
                }
            }
        }

        /// <summary>
        /// Get Gps From Device
        /// </summary>
        /// <param name="state"></param>
        /// <param name="paramArray"></param>
        private async static Task ProcessLoCDevice(StateObject state, string[] paramArray)
        {
            if (paramArray.Length > 4)
            {
                double lat = 0, lon = 0;
                if (!string.IsNullOrEmpty(paramArray[5]) && paramArray[5].ToLower() != "nan")
                {
                    if (paramArray[5].Split(':')[1] != ",,,,,,,,")
                    {
                        lat = int.Parse(paramArray[5].Split(':')[1].Split(",")[0].Substring(0, 2)) + double.Parse(paramArray[5].Split(':')[1].Split(",")[0].Substring(2, 6)) / 60;
                        lon = int.Parse(paramArray[5].Split(':')[1].Split(",")[2].Substring(0, 3)) + double.Parse(paramArray[5].Split(':')[1].Split(",")[2].Substring(3, 6)) / 60;
                         float.TryParse(paramArray[4].Split(':')[1], out float cpuTemp);

                         DateTime.TryParse(paramArray[3].Substring(5, paramArray[3].Length - 5), out DateTime fromDevice);

                        if (state.IMEI1 != null && state.IsConnected)
                        {                            
                            _ = UpdateMachineStateByMasterIMEI(
                                state.IMEI1, 
                                true,
                                lat.ToString(),
                                lon.ToString() 
                                ).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        private  static void ProcessLowBatteryDevice(StateObject state, string[] paramArray)
        {
             Console.WriteLine($"Device by IMEI1 ={state.IMEI1},IMEI2 ={state.IMEI2} and Ip {state.IP} Low Battey");
        }
        /// <summary>
        /// Download newFIle  Done and Update Device Version Compelete
        /// </summary>
        /// <param name="stateobject"></param>
        /// <param name="paramArray"></param>
        /// <returns></returns>
        private static async Task ProcessRPLRecievedContent(StateObject stateobject, string[] paramArray)
        {
            try
            {
                 int.TryParse(paramArray[1].Split(':')[1], out int VID);
                if (VID > 0) //Version Id Check
                {
                    try
                    {
                        if (AsynchronousSocketListener.SockeList.Exists(x => x.IMEI1 == stateobject.IMEI1 ))
                        {
                            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
                            {
                                try
                                {
                                    string sql = $"INSERT MachineVersionDetail (VersionId,State,CreateDate,Sender,Reciever) VALUES" +
                                        $"(@VersionId,@State,@CreateDate,@Sender,@Reciever)";
                                    using (SqlCommand command = new SqlCommand(sql, connection))
                                    {
                                         command.CommandTimeout = 100000;
                                         command.CommandType = CommandType.Text;
                                         command.Parameters.AddWithValue("@VersionId", VID);
                                         command.Parameters.AddWithValue("@State", "RPL");
                                         command.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                                         command.Parameters.AddWithValue("@Sender", stateobject.IMEI1);
                                         command.Parameters.AddWithValue("@Reciever", "Server");
                                         connection.Open();
                                         await command.ExecuteScalarAsync().ConfigureAwait(false);
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        Console.WriteLine($"*************************************");
                                        Console.WriteLine($"Get RPL from IMEI1={stateobject.IMEI1}  Ip={stateobject.IP}");
                                        Console.WriteLine($"*************************************");
                                        Console.ForegroundColor = ConsoleColor.Green;
                                    }
                                }
                                catch (Exception ex)
                                {
                                     _=LogErrorAsync(ex, "100 --Method-- ProcessRPLRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
                                }
                                finally
                                {
                                    connection.Close();
                                }
                                try
                                {
                                    //update IsDone in  machineVersion Table ==>  mean...Update Device Finished 
                                    string sql = $"Update MachineVersion Set IsDone = 1 , CompleteDate = @CompleteDate " +
                                                $" where Id = @VersionId";
                                    using (SqlCommand command = new SqlCommand(sql, connection))
                                    {
                                        command.CommandTimeout = 100000;
                                        command.CommandType = CommandType.Text;
                                         command.Parameters.AddWithValue("@VersionId", VID);
                                         command.Parameters.AddWithValue("@CompleteDate", DateTime.Now);
                                        connection.Open();
                                         await command.ExecuteScalarAsync().ConfigureAwait(false);
                                        try
                                        {
                                            int machineId = 0;
                                            string fileDownload = string.Empty;
                                            using (SqlCommand cc = new SqlCommand(sql, connection))
                                            {
                                                cc.CommandText = $"Select * from MachineVersion where Id={VID}";
                                                using var reader = await cc.ExecuteReaderAsync().ConfigureAwait(false);
                                                if (await reader.ReadAsync())
                                                {
                                                    fileDownload = reader["FileDownloadAddress"].ToString();
                                                     int.TryParse(reader["MachineId"].ToString(), out machineId);
                                                }
                                            }
                                            if (!string.IsNullOrEmpty(fileDownload) && machineId > 0) //Update Version In Machine 
                                            {
                                                var ar = fileDownload.Split('/');
                                                var versionNum = ar[ar[0].Length - 1]; //FileName format : nameFile-VersionNumb.zip  => config-1.0.0.zip                           

                                                command.CommandText = "Update Machine set Version=@NewVersion  where Id=@machineId";
                                                command.Parameters.Clear();
                                                try {
                                                     command.Parameters.AddWithValue("@NewVersion", versionNum.Split('-')[1].Substring(0, 5));//Second part of FileName is Version
                                                     command.Parameters.AddWithValue("@machineId", machineId);
                                                     await command.ExecuteScalarAsync().ConfigureAwait(false);
                                                    Console.ForegroundColor = ConsoleColor.Yellow;
                                                    Console.WriteLine($"*************************************");
                                                    Console.WriteLine($"Device by IMEI1={stateobject.IMEI1} IP={stateobject.IP} @ {DateTime.Now.ToString("yyyy-M-d HH-mm-ss",System.Globalization.CultureInfo.InvariantCulture)} Updated Compelete.");
                                                    Console.WriteLine($"*************************************");
                                                    Console.ForegroundColor = ConsoleColor.Green;
                                                }
                                                catch (Exception ex)
                                                {
                                                     _=LogErrorAsync(ex, "153 --Method-- ProcessRPLRecievedContent", $"IMEI1={stateobject.IMEI1},IMEI2={stateobject.IMEI2} Ip={stateobject.IP}").ConfigureAwait(false);
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _= LogErrorAsync(ex, "159 --Method-- ProcessRPLRecievedContent", $"IMEI1={stateobject.IMEI1},IMEI2={stateobject.IMEI2} Ip={stateobject.IP}").ConfigureAwait(false);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                     _=LogErrorAsync(ex, "165 --Method-- ProcessRPLRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
                                }
                                finally
                                {
                                    connection.Close();
                                }
                            }
                        }
                    }
                    catch (Exception ex) {

                         _=LogErrorAsync(ex, "176 --Method-- ProcessRPLRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                 _=LogErrorAsync(ex, "182 --Method-- ProcessRPLRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
            }
        }
        /// <summary>
        /// Finish newUpdate File With Device
        /// </summary>
        /// <param name="stateobject"></param>
        /// <param name="paramArray"></param>
        /// <returns></returns>
        private static async Task ProcessUPRRecievedContent(StateObject stateobject, string[] paramArray)
        {
            try
            {
                 int.TryParse(paramArray[1].Split(':')[1], out int VID);
                if (VID > 0) //Version Id Check
                {
                    try
                    {
                        if (AsynchronousSocketListener.SockeList.Exists(x => x.IMEI1 == stateobject.IMEI1))
                        {
                            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
                            {
                                string VIKey = "BgrUEy5IbpJSnhmqI2IhKw==";
                                try
                                {
                                    string sql = $"INSERT MachineVersionDetail (VersionId,State,CreateDate,Sender,Reciever) VALUES" +
                                        $"(@VersionId,@State,@CreateDate,@Sender,@Reciever)";
                                    using (SqlCommand command = new SqlCommand(sql, connection))
                                    {
                                        command.CommandTimeout = 100000;
                                        command.CommandType = CommandType.Text;
                                         command.Parameters.AddWithValue("@VersionId", VID);
                                         command.Parameters.AddWithValue("@State", "UPR");
                                         command.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                                         command.Parameters.AddWithValue("@Sender", stateobject.IMEI1);
                                         command.Parameters.AddWithValue("@Reciever", "Server");
                                        connection.Open();
                                         await command.ExecuteScalarAsync().ConfigureAwait(false);
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        Console.WriteLine($"*************************************");
                                        Console.WriteLine($"Get UPR Device by IMEI1={stateobject.IMEI1} Ip={stateobject.IP}");
                                        Console.WriteLine($"*************************************");
                                        Console.ForegroundColor = ConsoleColor.Green;
                                    }

                                }
                                catch (Exception ex)
                                {
                                     _=LogErrorAsync(ex, "225 -Method-- ProcessUPRRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
                                }
                                finally
                                {
                                    connection.Close();
                                }
                                if (CheckFileSizeAndFileName(VID, paramArray[2].ToString()).Result)
                                {
                                    //Send msg To Device for OK fileDownload
                                    var newContent = $"RPL#\"IMEI1\":\"{stateobject.IMEI1}\"";
                                    var content = (newContent+"#\"VID\":" + VID + "#").Encrypt("sample_shared_secret", VIKey);
                                    try
                                    {
                                        var sql = $"INSERT MachineVersionDetail (VersionId,State,CreateDate,Sender,Reciever) VALUES" +
                                                       $"(@VID,@State,@CreateDate,@Sender,@Reciever)";
                                        using (SqlCommand command = new SqlCommand(sql, connection))
                                        {
                                            command.CommandTimeout = 100000;
                                            command.CommandType = CommandType.Text;
                                             command.Parameters.AddWithValue("@VID", VID);
                                             command.Parameters.AddWithValue("@State", "RPL");
                                             command.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                                             command.Parameters.AddWithValue("@Sender", "Server");
                                             command.Parameters.AddWithValue("@Reciever", stateobject.IMEI1);
                                            connection.Open();
                                            //if (AsynchronousSocketListener.SocketConnected(stateobject))
                                            //{
                                             await command.ExecuteScalarAsync().ConfigureAwait(false);
                                            AsynchronousSocketListener.Send(stateobject.workSocket, VIKey + " ," + content);
                                            Console.ForegroundColor = ConsoleColor.Yellow;
                                            Console.WriteLine($"*************************************");
                                            Console.WriteLine($"Send RPL server To  IMEI1 ={stateobject.IMEI1} IP={stateobject.IP}");
                                            Console.WriteLine($"*************************************");
                                            Console.ForegroundColor = ConsoleColor.Green;
                                            //}
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                         _=LogErrorAsync(ex, "262 -Method-- ProcessUPRRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        connection.Close();
                                    }
                                }
                                else //Download file Nok
                                {
                                    var newContent = $"FSE#\"IMEI1\":\"{stateobject.IMEI1}\"";
                                    var content = (newContent+"#\"VID\":" + VID + "#").Encrypt("sample_shared_secret", VIKey);
                                    try
                                    {
                                        var sql = $"INSERT MachineVersionDetail (VersionId,State,CreateDate,Sender,Reciever) VALUES" +
                                                        $"(@VID,@State,@CreateDate,@Sender,@Reciever)";
                                        using (SqlCommand command = new SqlCommand(sql, connection))
                                        {
                                            command.CommandTimeout = 100000;
                                            command.CommandType = CommandType.Text;
                                             command.Parameters.AddWithValue("@VID", VID);
                                             command.Parameters.AddWithValue("@State", "FSE");
                                             command.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                                             command.Parameters.AddWithValue("@Sender", "Server");
                                             command.Parameters.AddWithValue("@Reciever", stateobject.IMEI1);
                                            connection.Open();
                                            //if (AsynchronousSocketListener.SocketConnected(stateobject))
                                            //{
                                             await command.ExecuteScalarAsync().ConfigureAwait(false);
                                            AsynchronousSocketListener.Send(stateobject.workSocket, VIKey + " ," + content);
                                            Console.ForegroundColor = ConsoleColor.Yellow;
                                            Console.WriteLine($"*************************************");
                                            Console.WriteLine($"Send FSE server To  IMEI1={stateobject.IMEI1} Ip ={stateobject.IP}");
                                            Console.WriteLine("File Size has Error");
                                            Console.WriteLine($"*************************************");
                                            Console.ForegroundColor = ConsoleColor.Green;
                                            //}
                                        }

                                    }
                                    catch (Exception ex)
                                    {
                                         _=LogErrorAsync(ex, "303 -- Method -- ProcessUPRRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
                                    }
                                    finally
                                    {
                                        connection.Close();
                                    }
                                }
                            }
                        }

                    }
                    catch (Exception ex)
                    {
                        _= LogErrorAsync(ex, "316 -- Method -- ProcessUPRRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _= LogErrorAsync(ex, "322 -Method-- ProcessUPRRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
            }
        }
        /// <summary>
        /// Check Size Client with File Size on Server
        /// </summary>
        /// <param name="VersionId">VersionId</param>        
        /// <param name="FileSize">Filesize should be Convert to byte</param>
        /// <returns></returns>
        private static async Task<bool> CheckFileSizeAndFileName(int VersionId, string FileSize)
        {
             long.TryParse(FileSize.Split(':')[1], out long sFileDownload);
            bool result = false;
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                try
                {
                    string sql = $"SELECT FileDownloadAddress from MachineVersion where Id = @Id ";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                         command.Parameters.AddWithValue("@Id", VersionId);
                        connection.Open();
                        var SelectedVersion = (string)await command.ExecuteScalarAsync().ConfigureAwait(false);
                        byte[] fileByte = await DownloadFile(SelectedVersion);
                        if (fileByte != null)
                        {
                            if (fileByte.Length == sFileDownload)
                            {
                                result = true;
                                return true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _ = LogErrorAsync(ex, "362 -Method-- CheckFileSizeAndFileName", FileSize.ToString());
                }
                finally
                {
                    connection.Close();
                }
            }
            return result;
        }
        /// <summary>
        /// Download File from Server
        /// </summary>
        /// <param name="url">Must be Full Url ,exp. Http://185.192.112.74/share/config.zip </param>
        /// <returns></returns>
        public static async Task<byte[]> DownloadFile(string url)
        {
            using (var client = new HttpClient())
            {

                using (var result = await client.GetAsync(url))
                {
                    if (result.IsSuccessStatusCode)
                    {
                        return await result.Content.ReadAsByteArrayAsync();
                    }
                }
            }
            return null;
        }
        /// <summary>
        /// Update Get Message
        /// </summary>
        /// <param name="stateobject"></param>
        /// <param name="paramArray"></param>
        /// <returns></returns>
        private static async Task ProcessUPGRecievedContent(StateObject stateobject, string[] paramArray)
        {
            try
            {
                 int.TryParse(paramArray[1].Split(':')[1], out int VID);
                if (VID > 0) //Version Id Check
                {
                    try
                    {
                        if (AsynchronousSocketListener.SockeList.Exists(x => x.IMEI1 == stateobject.IMEI1))
                        {
                            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
                            {
                                try
                                {
                                    string sql = $"INSERT MachineVersionDetail (VersionId,State,CreateDate,Sender,Reciever) VALUES" +
                                        $"(@VersionId,@State,@CreateDate,@Sender,@Reciever)";
                                    using (SqlCommand command = new SqlCommand(sql, connection))
                                    {
                                        command.CommandTimeout = 100000;
                                        command.CommandType = CommandType.Text;
                                        command.Parameters.AddWithValue("@VersionId", VID);
                                        command.Parameters.AddWithValue("@State", "UPG");
                                        command.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                                        command.Parameters.AddWithValue("@Sender", stateobject.IMEI1);
                                        command.Parameters.AddWithValue("@Reciever", "Server");
                                        connection.Open();
                                         await command.ExecuteScalarAsync().ConfigureAwait(false);
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        Console.WriteLine($"*************************************");
                                        Console.WriteLine($"Get UPG Device by IMEI1={stateobject.IMEI1} Ip={stateobject.IP}");
                                        Console.WriteLine($"*************************************");
                                        Console.ForegroundColor = ConsoleColor.Green;
                                    }
                                }

                                catch (Exception ex)
                                {
                                    _= LogErrorAsync(ex, "430 -Method-- ProcessUPGRecievedContent", $"IMEI1={stateobject.IMEI1} Ip={stateobject.IP}").ConfigureAwait(false);
                                }
                                finally
                                {
                                    connection.Close();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _= LogErrorAsync(ex, "412 -Method-- ProcessUPGRecievedContent", $"IMEI1={stateobject.IMEI1}  Ip={stateobject.IP}").ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                 _= LogErrorAsync(ex, "407 -Method-- ProcessUPGRecievedContent", $"IMEI1={stateobject.IMEI1}  Ip={stateobject.IP}").ConfigureAwait(false);
            }
        }
        /// <summary>
        /// This Method Get TSC Command And Analyze Parameter
        /// </summary>
        /// <param name="state">device</param>
        /// <param name="paramArray">parameter</param>
        /// <returns></returns>
        private static async Task ProcessTSCRecievedContent(StateObject state, string[] paramArray)
        {
            //LogErrorAsync(new Exception("Tsc Ana"), String.Join(" ", paramArray), String.Join(" ",state));//omid add
            try
            {
                if (paramArray.Length == 5) //mean get test by machine //one channel was 3
                {
                    //tsc#im1#im2#id(testId)  
                    await UpdateMachineTestStatusToRunning(state, paramArray[3].Split(':')[1]).ConfigureAwait(false);
                }
                if (paramArray.Length > 5) //one channel was 3
                {
                    if (paramArray.Contains("EndTest:TRUE"))//mean finish test //omid added --990606 
                    {                        
                        //if (paramArray[4].Split(':')[0] == "EndTest" && paramArray[4].Split(':')[1] == "TRUE")
                        //{  
                            //tsc#im1#im2#id(testId)#...#EndTest:TRUE
                            await UpdateMachineTestStatusToFinish(state, paramArray[3].Split(':')[1]).ConfigureAwait(false);
                       // }
                    }
                    else
                    {
                        string createDate = string.Empty;
                        bool cnt = false;
                        int TestId = 0; //omid added --981121                        
                        string inseretStatment = "insert into TestResult(";
                        string valueStatmenet = "Values(";
                        foreach (var param in paramArray)
                        {
                            string[] t = ProcessTSCParams(param);
                            if (t != null)
                            {
                                if (t.Length == 2)
                                {
                                    if (!t[0].Contains("TestId"))
                                    {
                                        int i;
                                        if (t[0] == "GPS")
                                        {
                                            double lon = 0;
                                            double lat = 0;
                                            //omid updated--98-11-28 , for nan value GPS
                                            if (!string.IsNullOrEmpty(t[1]) && t[1].ToLower() != "nan")
                                            {
                                                lat = int.Parse(t[1].Split(",")[0].Substring(0, 2)) + double.Parse(t[1].Split(",")[0].Substring(2, 6)) / 60;
                                                lon = int.Parse(t[1].Split(",")[2].Substring(0, 3)) + double.Parse(t[1].Split(",")[2].Substring(3, 6)) / 60;

                                                inseretStatment = inseretStatment + " Lat , Long, ";
                                                valueStatmenet = valueStatmenet + lat + " , " + lon.ToString() + " , ";
                                                //update machine location-- omid 99-01-04
                                                //10 Testresult will come together but only 1 Update Machine State Insert
                                                //if (!cnt)  //Dr.vahidPout 990230- all TestResult Update Machine State Insert
                                                //{                  
                                                var str = $"IMEI1=master is Mosh,IMEI1 = {state.IMEI1}IMEI2={state.IMEI2},lat={lat},lon={lon}";
                                                _ = LogErrorAsync(new Exception("UpdateMachineAfterHandShake"), str).ConfigureAwait(false);
                                                _ = Util.UpdateMachinesState(state.IMEI1, state.IMEI2, true, lat.ToString(), lon.ToString(), "ProcessTSCRecievedContent").ConfigureAwait(false);
                                                //   cnt = true;
                                                //}
                                            }
                                        }
                                        else if (t[0].Equals("Ping"))
                                        {
                                            var pingResult = t[1].Split(',');
                                            inseretStatment = inseretStatment + " NumOfPacketSent , NumOfPacketReceived, NumOfPacketLost, Ping, Rtt, MinRtt,AvgRtt,MaxRtt,mdev,  ";
                                            valueStatmenet = valueStatmenet + pingResult[0].Split(' ')[0] + " , " + pingResult[1].Split(' ')[1] + " , " + pingResult[2].Split('%')[0].Split(' ')[1] + " , 'Ping' ," +
                                                pingResult[3].Split('=')[0].Split(' ')[2].Split('m')[0] + " , " + pingResult[3].Split('=')[1].Split('/')[0] + " , " +
                                                pingResult[3].Split('=')[1].Split('/')[1] + " , " + pingResult[3].Split('=')[1].Split('/')[2] + " , " + pingResult[3].Split('=')[1].Split('/')[3].Split(' ')[0] + " ,";
                                        }
                                        else if (t[0].Equals("TraceRoute"))
                                        {
                                            inseretStatment = inseretStatment + "TraceRoute,hop1,hop1_rtt,hop2,hop2_rtt,hop3,hop3_rtt,hop4,hop4_rtt,hop5,hop5_rtt,hop6,hop6_rtt,hop7,hop7_rtt,hop8,hop8_rtt,hop9,hop9_rtt,hop10,hop10_rtt, ";

                                            var traceRtResponse = t[1].Split(',');
                                            var des = traceRtResponse[0];//destination
                                            valueStatmenet = valueStatmenet + $"'{des}' , ";

                                            //var hops = traceRtResponse[2].Split(new[] { "\n", "\r", "\n\r" }, StringSplitOptions.None);
                                            for (int j = 1; j <= 10; j++)
                                            {
                                                var Jindex = traceRtResponse[2].IndexOf($" {j} ");
                                                var Xindex = traceRtResponse[2].IndexOf($" {j + 1} ");
                                                string[] Jhop;
                                                if (Xindex == -1) //end of hops
                                                {
                                                    Jhop = traceRtResponse[2].Substring(Jindex).Split(' ');
                                                }
                                                else
                                                {
                                                    Jhop = traceRtResponse[2].Substring(Jindex, Xindex - Jindex).Split(' ');
                                                }
                                                if (Jhop[3] == "*")   //hop rtt
                                                {
                                                    valueStatmenet += "'" + Jhop[3] + "', " + System.Data.SqlTypes.SqlDouble.Null + " , ";
                                                }
                                                else
                                                {
                                                    valueStatmenet += "'" + Jhop[3] + "', " + Convert.ToDouble(Jhop[6]) + " , ";
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (t[0] == "CreateDate")
                                            {
                                                createDate = t[1].ToString();
                                            }

                                            inseretStatment = inseretStatment + t[0] + " ,";
                                            valueStatmenet = valueStatmenet + (int.TryParse(t[1].Replace("dBm", ""), out i) || t[1].Contains("0x") ? i.ToString() : "'" + t[1].Replace("dBm", "") + "'") + " ,";
                                        }
                                    }
                                    else
                                    {

                                        if (t[1].Contains("-"))
                                        {
                                            inseretStatment = inseretStatment + t[0] + " , IsGroup, ";
                                            valueStatmenet = valueStatmenet + t[1].Replace("-", "") + " , 1 , ";

                                            //int.TryParse(t[1].Replace("-", ""), out TestId); ////omid added --981121
                                        }
                                        else
                                        {
                                            inseretStatment = inseretStatment + t[0] + " , IsGroup, ";
                                            valueStatmenet = valueStatmenet + t[1] + " , 0 , ";

                                            int.TryParse(t[1], out TestId);//omid added --981121
                                        }
                                    }
                                }
                                else if (t.Length == 4)
                                {
                                    int.TryParse(t[1], out int mmc);
                                    int.TryParse(t[3], out int mnc);
                                    inseretStatment = inseretStatment + "MCC ,MNC,";
                                    valueStatmenet = valueStatmenet + mmc + " , " + mnc.ToString() + " , ";
                                }
                            }
                        }
                        var ExtraParam = await _GetParameterbyTestReultId(TestId); // omid added -- 98 11 21
                        await InsertTestResult(inseretStatment + " CreateDateFa, MachineId, MachineName, DefinedTestId, DefinedTestName,SelectedSim,BeginDateTest,EndDateTest ) " + valueStatmenet +
                                             $" cast([dbo].[CalculatePersianDate]('{createDate}')as nvarchar(max)) + N' '+cast(convert(time(0),'{createDate.Split(' ')[1]}') as nvarchar(max)),"
                                                                                + ExtraParam[1] + ", '" + ExtraParam[5] + "' ," + ExtraParam[0] + ",'" + ExtraParam[6] + "'," + ExtraParam[2] + ", '" + ExtraParam[3] + "' , '" + ExtraParam[4] + "' )", state);
                    }
                }
            }
            catch (Exception ex)
            {
                 _=LogErrorAsync(ex, "593 --Method-- ProcessTSCRecievedContent", $"IMEI1={state.IMEI1},IMEI2={state.IMEI2} Ip={state.IP}").ConfigureAwait(false);
            }
        }
        private static async Task InsertTestResult(string testResult, StateObject state)
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
                {
                    string sql = testResult;
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        connection.Open();
                         await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _=LogErrorAsync(ex, "615 --Method-- insertTestResult", $"IMEI1={state.IMEI1},IMEI2={state.IMEI2} Ip={state.IP}").ConfigureAwait(false);
            }
        }
        /// <summary>
        /// فراخوانی پارامترهای موردنیاز در گزارش
        /// omidAdd981121
        /// </summary>
        /// <param name="TestId">DefinedTestMachineId</param>
        /// <returns></returns>
        private static async Task<string[]> _GetParameterbyTestReultId(int TestId)
        {
            var res = new string[7];
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                try
                {
                    string sql = $"select dtm.DefinedTestId , dtm.MachineId ,dtm.SIM ,dtm.BeginDate , dtm.EndDate , m.Name, dt.Title" +
                                 $" from DefinedTestMachine as dtm" +
                                 $" left join Machine as m on dtm.MachineId = m.Id" +
                                 $" left join DefinedTest as dt on dtm.DefinedTestId = dt.id" +
                                 $" where dtm.id = {TestId}";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        connection.Open();
                        var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                        if (reader.Read())
                        {
                            //reader.Read();
                            //Console.WriteLine(reader);
                            res[0] = reader["DefinedTestId"].ToString();
                            res[1] = reader["MachineId"].ToString();
                            res[2] = reader["SIM"].ToString();
                            res[3] = reader["BeginDate"].ToString();
                            res[4] = reader["EndDate"].ToString();
                            res[5] = reader["Name"].ToString();//machine name
                            res[6] = reader["Title"].ToString(); //Test name
                        }
                        reader.Close();
                    }
                }
                catch (Exception ex)
                {
                     _=LogErrorAsync(ex, "658 - Method-- _GetParameterbyTestReultId").ConfigureAwait(false);
                }
                finally
                {
                    connection.Close();
                }
            }
            return res;
        }
        
        private static string[] ProcessTSCParams(string param)
        {
            if (param.Contains(":"))
            {
                float tmpVal;
                switch (param.Split(":")[0])
                {
                    case "Id":
                        return new string[] { "TestId", param.Split(":")[1] };
                    case "BER":
                        int ber;  int.TryParse(param.Split(":")[1], out ber);
                        if (ber >= 99) //مقدار غیر صحیح
                            return new string[] { "BER", DBNull.Value.ToString() };
                        else
                            return new string[] { "BER", param.Split(":")[1] };
                    case "PID":
                        return new string[] { "PID", param.Split(":")[1] };
                    case "MCC-MNC":
                        var vals = param.Split(":")[1].Split("-");
                        return new string[] { "MCC", vals[0], "MNC", vals[1] };
                    case "MCC":
                        return new string[] { "MCC", param.Split(":")[1] };
                    case "MNC":
                        return new string[] { "MNC", param.Split(":")[1] };
                    case "BSIC":
                        return new string[] { "BSIC", param.Split(":")[1] };
                    case "FrequencyBand": //omid updated
                        return new string[] { "FregBand", param.Split(":")[1] };
                    case "CID":
                        int cidValue;  int.TryParse(param.Split(":")[1], out cidValue);
                        if (cidValue == -1) //مقدار غیر صحیح
                            return new string[] { "CID", DBNull.Value.ToString() };
                        else
                            return new string[] { "CID", param.Split(":")[1] };
                    case "UARFCN":
                        return new string[] { "UARFCN", param.Split(":")[1] };
                    case "ARFCN":
                        return new string[] { "ARFCN", param.Split(":")[1] };
                    case "DLBW":
                        return new string[] { "DLBW", param.Split(":")[1] };
                    case "LAC":
                        int resL = 0;
                        if ((param.Split(":")[1]).Contains("0x")) //اگر هگزاست تبدیل شود به دسیمال
                        {
                            resL = Convert.ToInt32(param.Split(":")[1], 16);
                        }
                        else
                        {
                             int.TryParse(param.Split(":")[1], out resL);
                        }
                        if (resL == 0) //مقدار غیر صحیح
                            return new string[] { "LAC", DBNull.Value.ToString() };
                        return new string[] { "LAC", resL.ToString() };
                    case "ULBW":
                        return new string[] { "ULBW", param.Split(":")[1] };
                    case "BCCH":
                        return new string[] { "BCCH", param.Split(":")[1] };
                    case "RSSNR":
                        return new string[] { "RSSNR", param.Split(":")[1] };
                    case "TA":
                        return new string[] { "TA", param.Split(":")[1] };
                    case "PSC":
                        return new string[] { "PSC", param.Split(":")[1] };
                    case "EARFCN":
                        return new string[] { "EARFCN", param.Split(":")[1] };
                    case "TXPWR":
                        return new string[] { "TXPower", param.Split(":")[1] };
                    case "SSC":
                        return new string[] { "SSC", param.Split(":")[1] };
                    case "TAC":
                        int res = 0;
                        if ((param.Split(":")[1]).Contains("0x")) //اگر هگزاست تبدیل شود به دسیمال
                        {
                            res = Convert.ToInt32(param.Split(":")[1], 16);
                        }
                        else
                        {
                             int.TryParse(param.Split(":")[1], out res);
                        }
                        return new string[] { "TAC", res.ToString() };
                    case "RXLEV":
                        return new string[] { "RXLevel", param.Split(":")[1] };
                    case "ECIO":
                         float.TryParse((param.Split(":")[1]).ToString(), out tmpVal); tmpVal *= -1; //addedby-omid-981227
                        return new string[] { "ECIO", tmpVal.ToString() };
                    case "RSRQ":
                         float.TryParse(param.Split(":")[1], out tmpVal); tmpVal /= 10; //addedby-omid-981227
                        return new string[] { "RSRQ", tmpVal.ToString() };
                    case "RSCP":
                         float.TryParse(param.Split(":")[1], out tmpVal); tmpVal *= -1;//addeddby-omid-981227
                        return new string[] { "RSCP", tmpVal.ToString() };
                    case "RSRP":
                         float.TryParse(param.Split(":")[1], out tmpVal); tmpVal /= 10;//addeddby-omid-981227
                        return new string[] { "RSRP", tmpVal.ToString() };
                    case "RSSI":
                         float.TryParse(param.Split(":")[1], out tmpVal); tmpVal /= 10;//addeddby-omid-981229
                        return new string[] { "RSSI", tmpVal.ToString() };
                    case "OVFSF":
                        return new string[] { "OVFSF", param.Split(":")[1] }; //omid Edit and update
                    case "RXEQUAL":
                        return new string[] { "RXQual", param.Split(":")[1] };
                    case "SYSMODE":  //1:2G , 4:3G, 8:4G
                        return new string[] { "SystemMode", param.Split(":")[1] };
                    case "PingResault":
                        return new string[] { "Ping", param.Split(":")[1] };
                    case "OPERATOR":
                        return new string[] { "Operator", param.Split(":")[1] };//addeddby-omid-981229
                    case "Traceroute":
                        return new string[] { "TraceRoute", param.Split(":")[1] };
                    case "TIME":
                        return new string[] { "CreateDate", param.Split(":")[1] + ":" + param.Split(":")[2] + ":" + param.Split(":")[3] };
                    case "GPS":
                        return new string[] { "GPS", param.Split(":")[1] };
                    case "Layer3":
                        return new string[] { "Layer3Messages", param.Split(":")[1] };
                    case "SPEED": //HTTP-FTP-Downlink/Uplink  --during action--addedby omid 990107
                        double spd = 0;
                         double.TryParse(param.Split(":")[1], out spd);
                        return new string[] { "Speed", spd.ToString() };
                    case "ElapsedTime": //HTTP-FTP-Downlink/Uplink --compelete Action --addedby omid 990107
                        double ept = 0;
                         double.TryParse(param.Split(":")[1], out ept);                      
                        return new string[] { "ElapsedTime", ept.ToString() };
                    case "AvrgSpeed": //HTTP-FTP-Downlink/Uplink  --compelete Action--addedby omid 990107
                        double asp = 0;
                         double.TryParse(param.Split(":")[1], out asp);
                        return new string[] { "AvrgSpeed", asp.ToString() };
                    case "FileName": //MosCall Params , Name Of wav file                     
                        String FileName= param.Split(":")[1];
                        var ar = FileName.Split('/');
                        if (!string.IsNullOrEmpty(serverPath))
                        {
                            return new string[] { "FileName", serverPath + "/" + ar[ar.Length - 1] };
                        }
                        return new string[] { "FileName", ar[ar.Length-1] };
                    case "FileNameL3": //Layer3" ,Name Of txt file                        
                        var arr = param.Split(":")[1].Split('/');
                        if (!string.IsNullOrEmpty(serverPath))
                        {
                            return new string[] { "FileName", serverPath+"/"+ arr[arr.Length - 1] };
                        }
                        return new string[] { "FileName", arr[arr.Length-1] };                        
                    case "FileSize": //MosCall Params , Size Of wav file 
                    case "FileSizeL3"://layer3 , Size of txt file
                        int FileSize = 0;  int.TryParse(param.Split(":")[1], out FileSize);
                        return new string[] { "FileSize", FileSize.ToString() };
                    case "mosFile": //MosCall Params , Path oF File 
                    case "ServerL3"://layer3 params, Path of File
                        serverPath = param.Split(":")[1];
                        break;
                    default:
                        break;
                }
            }
            return null;
        }
        private static async Task UpdateMachineTestStatusToFinish(StateObject state, string testId)
        {           
                AsynchronousSocketListener.SendedTest.Add(testId);
                using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
                {
                    if (!testId.Contains("-"))//is not group
                    {
                        string sql = $"update DefinedTestMachine set status = 1, FinishTime = getdate() where id = @testId";
                        using (SqlCommand command = new SqlCommand(sql, connection))
                        {
                            command.CommandTimeout = 100000;
                            command.CommandType = CommandType.Text;
                         command.Parameters.AddWithValue("@testId", testId);
                            try
                            {
                                connection.Open();
                             await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                            catch(Exception ex)
                            {
                             _=LogErrorAsync(ex, "814 -- Method-- UpdateMachineTestStatusToFinish", $"IMEI1={state.IMEI1},IMEI2={state.IMEI2} Ip={state.IP}").ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        string sql = $"update DefinedTestMachineGroup set status = 1, FinishTime = getdate() where id = @testId";
                        using (SqlCommand command = new SqlCommand(sql, connection))
                        {
                            command.CommandTimeout = 100000;
                            command.CommandType = CommandType.Text;
                         command.Parameters.AddWithValue("@testId", testId.Replace("-", ""));
                            try
                            {
                                connection.Open();
                             await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                             _=LogErrorAsync(ex, " 833 -- Method-- UpdateMachineTestStatusToFinish", $"IMEI1={state.IMEI1},IMEI2={state.IMEI2} Ip={state.IP}").ConfigureAwait(false);
                            }
                        }
                    }                    
                }
        }
        private static async Task UpdateMachineTestStatusToRunning(StateObject state, string testId)
        {
            try
            {
                AsynchronousSocketListener.SendedTest.Add(testId);
                using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
                {
                    if (!testId.Contains("-"))
                    {
                        string sql = $"update DefinedTestMachine set status = 1 where id = @testId";
                        using (SqlCommand command = new SqlCommand(sql, connection))
                        {
                            command.CommandTimeout = 100000;
                            command.CommandType = CommandType.Text;
                             command.Parameters.AddWithValue("@testId", testId);
                            try
                            {
                                connection.Open();
                                 await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                            catch(Exception ex)
                            {
                                 _=LogErrorAsync(ex, "861 -- Method-- UpdateMachineTestStatusToRunning", $"IMEI1={state.IMEI1},IMEI2={state.IMEI2} Ip={state.IP}").ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        string sql = $"update DefinedTestMachineGroup set status = 1 where id = @testId";
                        using (SqlCommand command = new SqlCommand(sql, connection))
                        {
                            command.CommandTimeout = 100000;
                            command.CommandType = CommandType.Text;
                             command.Parameters.AddWithValue("@testId", testId.Replace("-", ""));
                            try
                            {
                                connection.Open();
                                 await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                 _=LogErrorAsync(ex, "881 -- Method-- UpdateMachineTestStatusToRunning", $"IMEI1={state.IMEI1},IMEI2={state.IMEI2} Ip={state.IP}").ConfigureAwait(false);
                            }
                        }
                    }                    
                }
            }
            catch (Exception ex) { 
            
                await LogErrorAsync(ex, "889 -- Method-- UpdateMachineTestStatusToRunning", $"IMEI1={state.IMEI1},IMEI2={state.IMEI2} Ip={state.IP}").ConfigureAwait(false);
            }
        }    
        private static async Task ProcessMIDRecievedContent(StateObject state, string[] paramArray)
        {
            try
            {
                state.IMEI1 = paramArray[1].Split(':')[1];                
                if (!AsynchronousSocketListener.SockeList.Exists(x => x.IMEI1 == state.IMEI1))
                {
                    AsynchronousSocketListener.SockeList.Add(state);
                    state.Timer.AutoReset = true;
                    state.lastDateTimeConnected = DateTime.Now;
                    state.Timer.Start();
                }
                else
                {
                    StateObject dd = AsynchronousSocketListener.SockeList.Find(x => x.IMEI1 == state.IMEI1);
                    dd.Timer.Stop();
                    //dd.workSocket.Shutdown(System.Net.Sockets.SocketShutdown.Both);
                    AsynchronousSocketListener.SockeList.Remove(dd);
                    AsynchronousSocketListener.SockeList.Add(state);
                    state.Timer.AutoReset = true;
                    state.lastDateTimeConnected = DateTime.Now;
                    state.Timer.Start();
                }                     
            }
            catch (Exception ex)
            {
                _ = LogErrorAsync(ex, "932 -- Method-- ProcessMIDRecievedContent", $"IMEI1={state.IMEI1},IMEI2={state.IMEI2} Ip={state.IP}").ConfigureAwait(false);
            }
        }
        private static string[] _CaptchaList = new string[100];
        static string SaltKey = "sample_salt";
        public static string Encrypt(this string plainText, string passwordHash, string VIKey)
        {
            byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainText);

            byte[] keyBytes = new Rfc2898DeriveBytes(passwordHash, Encoding.ASCII.GetBytes(SaltKey), 1024).GetBytes(16);
            var symmetricKey = new RijndaelManaged() { Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 };
            var encryptor = symmetricKey.CreateEncryptor(keyBytes, Convert.FromBase64String(VIKey));

            byte[] cipherTextBytes;

            using (var memoryStream = new MemoryStream())
            {
                using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
                {
                    cryptoStream.Write(plainTextBytes, 0, plainTextBytes.Length);
                    cryptoStream.FlushFinalBlock();
                    cipherTextBytes = memoryStream.ToArray();
                    cryptoStream.Close();
                }
                memoryStream.Close();
            }
            return Convert.ToBase64String(cipherTextBytes);
        }
        internal static async Task SendWaitingGroupTest(StateObject stateObject)        
        {
            string definedTest = "";
            string VIKey = "BgrUEy5IbpJSnhmqI2IhKw==";
            using (SqlConnection connection = new SqlConnection(ConnectionStrings))
            {
                try
                {
                    string sql = $"SELECT -1 * DTMG.Id Id, dt.Title, dt.Layer3Messages, case when dt.Layer3Messages =1 then '185.192.112.74/Uploads/L3Files' end ServerUrlL3, dt.RepeatTypeId, dt.RepeatTime, dt.RepeatCount, dt.MeasurementInterval, dt.TestTypeId, dt.UsualCallDuration, " +
                        $" dt.UsualCallWaitTime, dt.UsualCallNumber, dt.TestDataId, dt.TestDataTypeId, replace(replace(replace(case when(dt.TestDataDownloadFileAddress is null or dt.TestDataDownloadFileAddress = N'')then " +
                        $" dt.TestDataServer else dt.TestDataServer + N'/' + dt.TestDataDownloadFileAddress end ,N'//',N'/'),N'https:/',N''),N'http:/',N'') as TestDataServer, dt.TestDataUserName, dt.TestDataPassword , dt.TestDataUploadFileSize as FileSize, " +
                        $" dt.IPTypeId, dt.OTTServiceId, dt.OTTServiceTestId, dt.NetworkId, dt.BandId , dt.SaveLogFile, dt.LogFilePartitionTypeId, dt.LogFilePartitionTime, " +
                        $" dt.LogFilePartitionSize, dt.LogFileHoldTime, dt.NumberOfPings, dt.PacketSize, dt.InternalTime, dt.ResponseWaitTime, dt.TTL,replace(CONVERT(varchar(26), DTMG.BeginDate, 121), " +
                        $" N':', N'-') BeginDate, replace(CONVERT(varchar(26), DTMG.EndDate, 121), N':', N'-') EndDate, DTMG.SIM,  " +
                        $"                     case when TesttypeId not in(4, 2) then testtypeid " +
                        $"             when TestTypeId = 2 then '2' + cast(TestDataTypeId as nvarchar(10)) " +
                        $"             when TestTypeId = 4 then '4' + " +
                        $" 				case when testdataid in(3, 4) then cast(TestDataId as nvarchar(10)) " +
                        $"                      else cast(TestDataId as nvarchar(10)) + cast(TestDataTypeId as nvarchar(10)) end end TestType " +
                        $" from MachineGroup MG " +
                        $" join DefinedTestMachineGroup DTMG on MG.Id = DTMG.MachineGroupId " +
                        $" join DefinedTest DT on DTMG.DefinedTestId = DT.id " +
                        $" where DTMG.IsActive = 1 and DTMG.BeginDate > getdate() and " +
                        $" DTMG.IsActive = 1 and DTMG.Status = 0 /*status = 0, not test*/ " +
                        $" and MG.Id = (select MachineGroupId from machine where IMEI1 = @IMEI1 and IMEI2 = @IMEI2  )  for json path ";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        command.Parameters.AddWithValue("@IMEI1", stateObject.IMEI1);
                        command.Parameters.AddWithValue("@IMEI2", stateObject.IMEI2);
                        connection.Open();
                        definedTest = (string)await command.ExecuteScalarAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {

                     _ = LogErrorAsync(ex, "996 -- Method --  SendWaitingGroupTest", $"IMEI1={stateObject.IMEI1},IMEI2={stateObject.IMEI2} Ip={stateObject.IP}");
                }
                finally
                {
                    connection.Close();
                }
            }
            if (definedTest != null)
            {
                foreach (var item in JArray.Parse(definedTest))
                {
                    var content = ("TST#" + item.ToString().Replace("}", "").Replace(",", "#").Replace("{", "")).Replace(" ", "").Replace("\r", "").Replace("\n", "").ToString().Encrypt("sample_shared_secret", VIKey);
                    if (AsynchronousSocketListener.SendedTest.Find(t => t.Contains(content)) == null)
                    {
                        
                            AsynchronousSocketListener.Send(stateObject.workSocket, VIKey + " ," + content);
                    }
                }
            }
        }
        internal static async Task SendWaitingTest(Socket MasterSocket,string Imei1, string Imei2)
        {           
            string definedTest = "";
            string VIKey = "BgrUEy5IbpJSnhmqI2IhKw==";
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                try
                {
                    string sql = $"SELECT DTM.Id Id, dt.Title, dt.Layer3Messages, case when dt.Layer3Messages =1 then '185.192.112.74/Uploads/L3Files' end TestDataServerL3, " +
                        $" dt.RepeatTypeId, dt.RepeatTime, dt.RepeatCount, dt.MeasurementInterval, dt.TestTypeId, dt.UsualCallDuration, " +
                        $"dt.UsualCallWaitTime, dt.UsualCallNumber, dt.TestDataId, dt.TestDataTypeId, replace(replace(replace(case when (dt.TestDataDownloadFileAddress is null or dt.TestDataDownloadFileAddress = N'' )then  " +
                        $"dt.TestDataServer else dt.TestDataServer + N'/' + dt.TestDataDownloadFileAddress end ,N'//',N'/'),N'https:/',N''),N'http:/',N'') as TestDataServer, dt.TestDataUserName, dt.TestDataPassword , dt.TestDataUploadFileSize as FileSize, " +
                        $"dt.IPTypeId, dt.OTTServiceId, dt.OTTServiceTestId, dt.NetworkId, dt.BandId , dt.SaveLogFile, dt.LogFilePartitionTypeId, dt.LogFilePartitionTime, " +
                        $"dt.LogFilePartitionSize, dt.LogFileHoldTime, dt.NumberOfPings, dt.PacketSize, dt.InternalTime, dt.ResponseWaitTime, " +
                        $" dt.TTL,replace(CONVERT(varchar(26),DTM.BeginDate, 121) , " +
                        $"N':',N'-') BeginDate, replace(CONVERT(varchar(26),DTM.EndDate, 121),N':',N'-') EndDate, DTM.SIM,  " +
                        $"                    case when TesttypeId not in(4, 2) then testtypeid " +
                        $"            when TestTypeId = 2 then '2' + cast(TestDataTypeId as nvarchar(10)) " +
                        $"            when TestTypeId = 4 then '4' + " +
                        $"				case when testdataid in(3, 4) then cast(TestDataId as nvarchar(10)) " +
                        $"                     else cast(TestDataId as nvarchar(10)) + cast(TestDataTypeId as nvarchar(10)) end end TestType " +
                        $"from Machine M " +
                        $"join DefinedTestMachine DTM on M.Id = DTM.MachineId " +
                        $"join DefinedTest DT on DTM.DefinedTestId = DT.id " +
                        $"where DTM.IsActive = 1 and DTM.BeginDate > getdate() and " +
                        $"DTM.IsActive = 1 and DTM.Status = 0 " +/*status = 0, not test*/
                        $"and m.IMEI1 = @IMEI1 and m.IMEI2 = @IMEI2  for json path";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        command.Parameters.AddWithValue("@IMEI1", Imei1);
                        command.Parameters.AddWithValue("@IMEI2", Imei2);
                        connection.Open();
                        definedTest = (string)await command.ExecuteScalarAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _ = LogErrorAsync(ex, "1052 --Method-- SendWaitingTest", $"IMEI1={Imei1},IMEI2={Imei2}");
                }
                finally
                {
                    connection.Close();
                }
            }
            if (definedTest != null)
            {
                foreach (var item in JArray.Parse(definedTest))
                {
                    //Update 990525-- handle 6 channel
                    var newContent = $"TST#\"IMEI1\":\"{Imei1}\"#\"IMEI2\":\"{Imei2}\"#";
                    var content = (newContent + item.ToString().Replace("}", "").Replace(",", "#").Replace("{", "")).Replace(" ", "").Replace("\r", "").Replace("\n", "").ToString();
                    _= LogErrorAsync(new Exception("TST"), content, $"IMEI1={Imei1} IMEI2={Imei2}");
                     content = content.Encrypt("sample_shared_secret", VIKey);
                    //change content by testid --98-12-01
                    if (AsynchronousSocketListener.SendedTest.Find(t => t.Contains(content)) == null)
                    {                    
                       AsynchronousSocketListener.Send(MasterSocket, VIKey + " ," + content);
                    }
                }
            }           
        }
        internal static async Task UpdateVersion(StateObject stateObject)
        {
            string newUpdate = "";
            string VIKey = "BgrUEy5IbpJSnhmqI2IhKw==";
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                try
                {
                    string sql = $"SELECT r.* " +
                        $" from MachineVersion r " +
                        $" where r.IMEI1 = @IMEI1  and r.IsDone = 0" +
                        $" order by r.CreateDate Desc " +
                        $" for json path";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        command.Parameters.AddWithValue("@IMEI1", stateObject.IMEI1);
                        //command.Parameters.AddWithValue("@IMEI2", stateObject.IMEI2);
                        connection.Open();
                        newUpdate = (string)await command.ExecuteScalarAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                     _=LogErrorAsync(ex, "1117 -- Method -- UpdateVersion", $"IMEI1={stateObject.IMEI1} Ip={stateObject.IP}");
                }
                finally
                {                    
                    connection.Close();                   
                }
            }
            if (newUpdate != null)
            {
                var curReq = JArray.Parse(newUpdate);                
                //curReq["SendToDevice"] => 0:upd donot send To device
                if (!Convert.ToBoolean(curReq[0]["SendToDevice"].ToString()))
                {
                    string sql = string.Empty;
                    using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
                    {
                        try
                        {
                            //دستور آپدیت دستگاه با آدرس فایل مورد نظردر سرور
                            var Newcontent = $"UPD#\"IMEI1\":\"{stateObject.IMEI1}\"";
                            var content = (Newcontent+"#\"VID\":" + curReq[0]["Id"] + "#\"TestDataServer\":" + "\"" + curReq[0]["FileDownloadAddress"].ToString().Split(':')[1].Substring(2) + "\"")
                                        .Encrypt("sample_shared_secret", VIKey);
                            if (AsynchronousSocketListener.SockeList.Exists(x => x.IMEI1 == stateObject.IMEI1))
                            {
                                connection.Open();
                                sql = await Trans_AddVersionDetail(stateObject, VIKey, curReq, connection, sql, content);
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"*************************************");
                                Console.WriteLine($"Send UPD Server To IMEI1 ={ stateObject.IMEI1} IP={stateObject.IP}");
                                Console.WriteLine($"*************************************");
                                Console.ForegroundColor = ConsoleColor.Green;
                                _= Util.LogErrorAsync(new Exception($"UPD Send To IMEI1= {stateObject.IMEI1}"), content, $"IMEI1={stateObject.IMEI1} Ip={stateObject.IP}");
                            }
                        }
                        catch (Exception ex)
                        {
                             _=LogErrorAsync(ex, "1147 -- Method -- UpdateVersion", $"IMEI1={stateObject.IMEI1},IMEI2={stateObject.IMEI2} Ip={stateObject.IP}");
                        }
                        finally
                        {
                            connection.Close();                         
                        }
                    }
                }
                //curReq["SendToDevice"] => 1:upd Send To Device and Device Get upd message
                else
                {
                    string ExsitUPD;
                    using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
                    {
                        try
                        {
                            string sql = $"select * from MachineVersionDetail where VersionId = @VersionId and state = N'UPD' "+
                                 $" for json path";
                            using (SqlCommand command = new SqlCommand(sql, connection))
                            {
                                await connection.OpenAsync();
                                command.CommandTimeout = 100000;
                                command.CommandType = CommandType.Text;
                                 command.Parameters.AddWithValue("@VersionId", curReq[0]["Id"].ToString());
                                ExsitUPD = (string)await command.ExecuteScalarAsync().ConfigureAwait(false);
                                if (ExsitUPD == null) //if upd donot Exist, add Upd Recored and Send Updaet Message
                                {
                                    var newContent = $"UPD#\"IMEI1\":\"{stateObject.IMEI1}\"";
                                    var content = (newContent+"#\"VID\":" + curReq[0]["Id"] + "#\"TestDataServer\":" + "\"" + curReq[0]["FileDownloadAddress"].ToString().Split(':')[1].Substring(2) + "\"")
                                                              .Encrypt("sample_shared_secret", VIKey);
                                    try
                                    {
                                        if (AsynchronousSocketListener.SockeList.Exists(x => x.IMEI1 == stateObject.IMEI1))
                                        {
                                            sql = await Trans_AddVersionDetail(stateObject, VIKey, curReq, connection, sql, content);                                            
                                            Console.ForegroundColor = ConsoleColor.Yellow;
                                            Console.WriteLine($"*************************************");
                                            Console.WriteLine($"Send UPD Server To IMEI1={stateObject.IMEI1} IP={stateObject.IP}");
                                            Console.WriteLine($"*************************************");
                                            Console.ForegroundColor = ConsoleColor.Green;
                                            _= Util.LogErrorAsync(new Exception($"UPD Send To IMEI1= {stateObject.IMEI1}"), content, $"IMEI1={stateObject.IMEI1}, Ip={stateObject.IP}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                         _=LogErrorAsync(ex, "1193 -- Method -- UpdateVersion", $"IMEI1={stateObject.IMEI1}, Ip={stateObject.IP}");
                                    }
                                }
                                else //Server Send UPD Message To Device but Device donot Reply UPG
                                {   
                                        object ExsitUPG;
                                        sql = $"select * from MachineVersionDetail where VersionId = @VersionId and state = N'UPG' ";
                                        command.CommandTimeout = 100000;
                                        command.CommandType = CommandType.Text;                                        
                                        command.CommandText = sql;
                                    try
                                    {
                                        ExsitUPG = await command.ExecuteScalarAsync().ConfigureAwait(false);
                                        if (ExsitUPG == null) // را ارسال نکرده باشدUPG کلاینت هنوز پیام 
                                        {
                                            var curUPDRec = JArray.Parse(ExsitUPD);
                                            var UPDCreateDate = Convert.ToDateTime(curUPDRec[0]["CreateDate"]);
                                            // سرور به کلاینت گذشته باشد، اطلاعات دوباره ارسال میشود UPDو دو دقیقه  هم از زمان ارسال پیام 
                                            TimeSpan diffGT2 = DateTime.Now - UPDCreateDate;
                                            if (diffGT2.Seconds > 60)
                                            {
                                                try
                                                {
                                                    int detail_UpdId;  int.TryParse(curUPDRec[0]["Id"].ToString(), out detail_UpdId);
                                                    sql = await Trans_DelVersionDetail(curReq, connection, sql, detail_UpdId, stateObject.IMEI1);
                                                    //ارسال دوباره فرمان آپدیت                                        
                                                    //دستور آپدیت دستگاه با آدرس فایل مورد نظردر سرور
                                                    var newContent = $"UPD#\"IMEI1\":\"{stateObject.IMEI1}\"";
                                                    var content = (newContent+"#\"VID\":" + curReq[0]["Id"] + "#\"TestDataServer\":" + "\"" + curReq[0]["FileDownloadAddress"].ToString().Split(':')[1].Substring(2) + "\"")
                                                                                                .Encrypt("sample_shared_secret", VIKey);
                                                    try
                                                    {
                                                        if (AsynchronousSocketListener.SockeList.Exists(x => x.IMEI1 == stateObject.IMEI1))
                                                        {
                                                            sql = await Trans_AddVersionDetail(stateObject, VIKey, curReq, connection, sql, content);                                                            
                                                            Console.ForegroundColor = ConsoleColor.Yellow;
                                                            Console.WriteLine($"*************************************");
                                                            Console.WriteLine($"Send UPD Server To IMEI1={stateObject.IMEI1} Ip={stateObject.IP}");
                                                            Console.WriteLine($"*************************************");
                                                            Console.ForegroundColor = ConsoleColor.Green;
                                                            _= Util.LogErrorAsync(new Exception($"UPD Send To IMEI1={stateObject.IMEI1}, IP={stateObject.IP}"), content, $"IMEI1={stateObject.IMEI1} Ip={stateObject.IP}");
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                         _=LogErrorAsync(ex, "1236 -- Method -- UpdateVersion", $"IMEI1={stateObject.IMEI1} Ip={stateObject.IP}");
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                     _=LogErrorAsync(ex, "1241 -- Method -- UpdateVersion", $"IMEI1={stateObject.IMEI1} Ip={stateObject.IP}");
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                         _=LogErrorAsync(ex, "1248 -- Method -- UpdateVersion", $"IMEI1={stateObject.IMEI1} Ip={stateObject.IP}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                             _=LogErrorAsync(ex, "1255 -- Method -- UpdateVersion", $"IMEI1={stateObject.IMEI1} Ip={stateObject.IP}");
                        }
                        finally
                        {
                            connection.Close();                            
                        }
                    }
                }
            } 
        }
        public static async Task<string> Trans_AddVersionDetail(StateObject stateObject, string VIKey, JToken item, SqlConnection connection, string sql, string content)
        {
            var tx = connection.BeginTransaction();
            try
            {
                sql = $" INSERT MachineVersionDetail (VersionId,State,CreateDate,Sender,Reciever) VALUES" +
                   $" ({item[0]["Id"].ToString()},@State,@CreateDate,@Sender,@Reciever); select SCOPE_IDENTITY()";
                var com2 = new SqlCommand(sql, connection);
                com2.CommandTimeout = 100000;
                com2.Transaction = tx;
                 com2.Parameters.AddWithValue("@State", "UPD");
                 com2.Parameters.AddWithValue("@CreateDate", DateTime.Now);
                 com2.Parameters.AddWithValue("@Sender", "Server");
                 com2.Parameters.AddWithValue("@Reciever", stateObject.IMEI1);
                object id = await com2.ExecuteScalarAsync().ConfigureAwait(false);
                //update SendToDevice in  machineVersion Table ==>  mean...Device Get this Update                                                     
                try
                {
                    sql = $" Update MachineVersion Set SendToDevice = 1  where Id = @VersionId";
                    var com3 = new SqlCommand(sql, connection);
                    com3.CommandTimeout = 100000;
                    com3.CommandType = CommandType.Text;
                    com3.Transaction = tx;
                     com3.Parameters.AddWithValue("@VersionId", item[0]["Id"].ToString());
                    id = await com3.ExecuteScalarAsync().ConfigureAwait(false);
                    //if (AsynchronousSocketListener.SocketConnected(stateObject))
                    //{
                        tx.Commit();
                        AsynchronousSocketListener.Send(stateObject.workSocket, VIKey + " ," + content);
                    //}
                }
                catch (Exception ex)
                {
                     _=LogErrorAsync(ex, "1299 -- Method -- Trans_AddVersionDetail", stateObject.IMEI1);
                    tx.Rollback();
                }
            }
            catch (Exception ex)
            {
                 _=LogErrorAsync(ex, "1309 -- Method -- Trans_AddVersionDetail", stateObject.IMEI1);
                tx.Rollback();
            }
            return sql;
        }
        public static async Task<string> Trans_DelVersionDetail(JToken item, SqlConnection connection, string sql,int VersionDetailId,string IMEI1)
        {
            var tx = connection.BeginTransaction();
            try
            {
                sql = $"Delete from MachineVersionDetail where Id =@Id";
                var com2 = new SqlCommand(sql, connection);
                com2.CommandTimeout = 100000;
                com2.Transaction = tx;
                 com2.Parameters.AddWithValue("@Id", VersionDetailId);                
                object id = await com2.ExecuteScalarAsync().ConfigureAwait(false);                
                try
                {
                    sql = $"Update MachineVersion Set SendToDevice=0 where Id = @VersionId";
                    var com3 = new SqlCommand(sql, connection);
                    com3.CommandTimeout = 100000;
                    com3.CommandType = CommandType.Text;
                    com3.Transaction = tx;
                     com3.Parameters.AddWithValue("@VersionId", item[0]["Id"].ToString());
                    id = await com3.ExecuteScalarAsync().ConfigureAwait(false);                    
                    tx.Commit();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Send UPD To Device after 2 minute,again.beacuse device Don't Respond");
                    Console.ForegroundColor = ConsoleColor.Green;
                    _=Util.LogErrorAsync(new Exception("Send UPD To Device after 2 minute again"), "Send UPD To Device after 2 minute again",  $"Master IMEI={IMEI1}" ).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                     _=LogErrorAsync(ex, "1339 -- Method -- Trans_DelVersionDetail",$"Master IMEI={IMEI1}");
                    tx.Rollback();
                }              
            }
            catch (Exception ex)
            {
                _=LogErrorAsync(ex, "1345 -- Method -- Trans_DelVersionDetail",  $"Master IMEI={IMEI1}");
                tx.Rollback();
            }
            return sql;
        }
        public static string Decrypt(this string encryptedText, string passwordHash, string VIKey)
        {
            byte[] cipherTextBytes = Convert.FromBase64String(encryptedText);
            byte[] keyBytes = new Rfc2898DeriveBytes(passwordHash, Encoding.ASCII.GetBytes(SaltKey), 1024).GetBytes(16);
            var symmetricKey = new RijndaelManaged() { Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 };
            var decryptor = symmetricKey.CreateDecryptor(keyBytes, Convert.FromBase64String(VIKey));
            var memoryStream = new MemoryStream(cipherTextBytes);
            var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
            byte[] plainTextBytes = new byte[cipherTextBytes.Length];
            int decryptedByteCount = cryptoStream.Read(plainTextBytes, 0, plainTextBytes.Length);
            memoryStream.Close();
            cryptoStream.Close();
            return Encoding.UTF8.GetString(plainTextBytes, 0, decryptedByteCount).TrimEnd("\0".ToCharArray());
        }
        internal static async Task UpdateMachinesState(string IMEI1, string IMEI2, bool IsConnected, string lat, string lon,string Callfrom, DateTime? DateFromDevice = null, float? CpuTemprature = 0)
        {
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                try
                {
                    string sql = $"if not exists(select 1 from machine where IMEI1 = @IMEI1 and IMEI2 = @IMEI2) " +
                        $"begin " +
                        $" insert into machine(IMEI1, IMEI2, MachineTypeId) select @IMEI1, @IMEI2, 1 " +
                        $"end " +
                        $"update machine set IsConnected = @IsConnected, Latitude = case when @Lat !=N'0'  then @Lat else Latitude end, " +
                        $" Longitude = case when @Lon != N'0' then @Lon else Longitude end where IMEI1 = @IMEI1 and IMEI2 = @IMEI2 " +
                        $"insert into MachineConnectionHistory( MachineId, IsConnected,Latitude,Longitude,DateFromDevice,CpuTemperature) values " +
                        $" ((select top 1 id from  machine where IMEI1 = @IMEI1 and IMEI2 = @IMEI2 ), @IsConnected,@Lat,@Lon,@fromDevice,@degree)";
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                         command.Parameters.AddWithValue("@IsConnected", IsConnected);
                         command.Parameters.AddWithValue("@IMEI1", IMEI1);
                         command.Parameters.AddWithValue("@IMEI2", IMEI2);
                         command.Parameters.AddWithValue("@Lat", lat);
                         command.Parameters.AddWithValue("@Lon", lon);
                        if (DateFromDevice != null)
                        {
                             command.Parameters.AddWithValue("@fromDevice", DateFromDevice);
                        }
                        else
                        {
                             command.Parameters.AddWithValue("@fromDevice", DBNull.Value);
                        }
                          command.Parameters.AddWithValue("@degree", CpuTemprature);
                          connection.Open();
                         await command.ExecuteNonQueryAsync();
                        //بروزرسانی لیست ماشین ها
                        if (IsConnected)
                        {
                            //add device to device list
                            lock (AsynchronousSocketListener.threadLock)
                            {
                                if (!AsynchronousSocketListener.DeviceList.Exists(x => x.IMEI2 == IMEI2 && x.IMEI1 == IMEI1))
                                {
                                    AsynchronousSocketListener.DeviceList.Add(new Machine
                                    {
                                        CreateDate = DateTime.Now,
                                        IMEI1 = IMEI1,
                                        IMEI2 = IMEI2
                                    });
                                }
                            }
                        }
                        else
                        {
                            lock (AsynchronousSocketListener.threadLock)
                            {
                                //remvoe device from device list
                                if (!AsynchronousSocketListener.DeviceList.Exists(x => x.IMEI2 == IMEI2 && x.IMEI1 == IMEI1))
                                {
                                    var dicDevicfromMaster = AsynchronousSocketListener.DeviceList.FirstOrDefault(x => x.IMEI1 == IMEI1 && x.IMEI1 == IMEI1);
                                    AsynchronousSocketListener.DeviceList.Remove(dicDevicfromMaster);
                                }
                            }
                        }                        
                    }
                }
                catch (Exception ex)
                {
                    var str = $"IMEI1={IMEI1},IMEI2={IMEI2},IsConnected={IsConnected},lat={lat},lon={lon}";
                     _=LogErrorAsync(ex, $"1393 -- Method -- UpdateMachineState --{Callfrom}",str);
                }
                finally
                {
                    connection.Close();
                }
            }
        }
        //Update device by Master
        internal static async Task UpdateMachineStateByMasterIMEI(string IMEI1,  bool IsConnected, string lat="0",string lon="0")
        {
            using (SqlConnection connection = new SqlConnection(Util.ConnectionStrings))
            {
                try
                {
                    string sql =
                        $" update machine set IsConnected = @IsConnected, Latitude = case when @Lat !=N'0'  then @Lat else Latitude end, " +
                        $" Longitude = case when @Lon != N'0' then @Lon else Longitude end where IMEI1 = @IMEI1 " +
                        $" DECLARE @machinId int " +
                        $" DECLARE machineforMaster CURSOR for " +
                        $" SELECT DISTINCT Id " +
                        $" FROM machine Where IMEI1= @IMEI1 " +
                        $" OPEN machineforMaster " +
                        $" FETCH NEXT FROM machineforMaster INTO @machinId " +
                        $" WHILE @@FETCH_STATUS = 0 " +
                        $" BEGIN " +
                            $" insert into MachineConnectionHistory(MachineId, IsConnected,Latitude,Longitude) values " +
                            $" (@machinId, @IsConnected,@Lat,@Lon) " +
                            $" FETCH NEXT FROM machineforMaster INTO @machinId " +
                        $" END " +
                        $" CLOSE machineforMaster " +
                        $" DEALLOCATE machineforMaster";                       
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        command.CommandTimeout = 100000;
                        command.CommandType = CommandType.Text;
                        command.Parameters.AddWithValue("@IsConnected", IsConnected);
                        command.Parameters.AddWithValue("@IMEI1", IMEI1);                        
                        command.Parameters.AddWithValue("@Lat", lat);
                        command.Parameters.AddWithValue("@Lon", lon);                       
                        connection.Open();
                        await command.ExecuteNonQueryAsync();                                    
                    }
                }
                catch (Exception ex)
                {
                    _ = LogErrorAsync(ex, "1393 -- Method -- UpdateMachineStateByMasterIMEI", IMEI1 );
                }
                finally
                {
                    connection.Close();
                }
            }
        }
        public async static Task LogErrorAsync(Exception exception, string business, string ip = null)
        {
            
            string methdeName = "";
            string moduleName = "";
            try
            {

                var st = new StackTrace(exception, true);
                var frame = st.GetFrame(0);
                methdeName = string.Format("{0}.{1}", frame.GetMethod().DeclaringType.FullName, exception.TargetSite.ToString());
                moduleName = exception.TargetSite.DeclaringType.Module.Name;
                var assemblyName = exception.TargetSite.DeclaringType.Assembly.FullName;
                
            }
            catch {
                // Console.WriteLine("Error In LogErrorAsync Error:{0} \n MethodNamd or MoudleName don't have value", e.Message);                
            }
            if (exception.Data.Count > 0)
            {
                 exception.Data.ToJsonString();
            }
            using (SqlConnection con = new SqlConnection(Util.ConnectionStrings))
            {
                var com = con.CreateCommand();
                com.CommandText = @"INSERT INTO [system].[Errors]
                                   ([Date]
                                   ,[Business]
                                   ,[Module]
                                   ,[Methode]
                                   ,[Message]
                                   ,[RawError]
                                   ,[ExtraData])
                             VALUES
                                   (GETDATE()
                                   ,@Business
                                   ,@Module
                                   ,@Methode
                                   ,@Message
                                   ,@RawError
                                   ,@ip);
                            SELECT @@IDENTITY";
                 com.Parameters.AddWithValue("@Business", business);
                 com.Parameters.AddWithValue("@Module", moduleName);
                 com.Parameters.AddWithValue("@Methode", methdeName);
                com.Parameters.AddWithValue("@Message", exception.Message + "----" + exception.StackTrace);
                 com.Parameters.AddWithValue("@RawError", exception.ToString());
                 com.Parameters.AddWithValue("@ip", ip ?? "");
                try
                {
                    await con.OpenAsync();
                     await com.ExecuteScalarAsync();
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error In Save Error:{0}", e);
                    Console.WriteLine("Stack Trace \n ",Environment.StackTrace);
                    //throw e; //98-12-1
                }
                finally
                {
                    con.Close();
                }
            }
        }
        public static string ToJsonString(this object obj)
        {
            string retVal = null;
            if (obj != null)
            {
                retVal = JsonConvert.SerializeObject(obj, Formatting.Indented);
            }
            return retVal;
        }

    }
}
