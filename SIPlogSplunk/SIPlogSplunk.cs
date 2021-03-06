﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Splunk.Client;
using System.Globalization;
using System.Security;
using System.Net;
using System.Diagnostics;

public class SipSplunk
{
    string beginMsgRgxStr = @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}.\d{6}.*\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}.*\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}"; //regex to match the begining of the sip message (if it starts with a date and has time and two IP addresses)  for tcpdumpdump
    string acBeginMsgRgxStr = @".srcip=(?<SrcIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}).*dstip=(?<DstIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}).*Sent:(?<timedate>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}.\d{3}.\d{2}:\d{2}).*(?<req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))";
    string acSyslogBeginMsgRgxStr = @".srcip=(?<SrcIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}).*dstip=(?<DstIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}).*\d{2}:\d{2}:\d{2}.(?<ms>\d{3}).*(?<req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))";
    string acSyslogTimeRgxStr = @"\[Time:(?<day>\d{2})-(?<month>\d{2})@(?<time>\d{2}:\d{2}:\d{2})\]";
    string dateRgxStr = @"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}.\d{6})"; //for tcpdumpdump 
    string srcIpRgxStr = @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?=(.|:)\d* >)";    
    string dstIpRgxStr = @"(?<=> )(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})";
    string requestRgxStr = @"ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*)";
    //string callidRgxStr = @"(?<!-.{8})(?<=Call-ID:)\S*";//do not match if -Call-ID instead of Call-ID
    string callidRgxStr = @"(?<!-.{8})(?<=Call-ID:)\s* (\S*)";
    string toRgxStr = @"(?<=To:) *(\x22.+\x22)? *<?(sip:)([^@>]+)";
    string fromRgxStr = @"(?<=From:) *(\x22.+\x22)? *<?(sip:)([^@>]+)";
    string uaRgxStr = @"(?<=User-Agent:).*";
    string serverRgxStr = @"(?<=Server:).*";
    string portRgxStr = @"(?<=m=audio )\d*";
    string codecRgxStr = @"(?<=RTP\/AVP )\d*";
    string SDPIPRgxStr = @"(?<=c=IN IP4 )(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})";
    string mAudioRgxStr = @"m=audio \d* RTP\/AVP \d*";
    string occasRgxStr = @"(?<=Contact: ).*wlssuser";
    string cseqRgxStr = @"CSeq:\s?(\d{1,3})\s?(\w*)";
    Regex beginmsgRgx;
    Regex acBeginMsgRgx;
    Regex acSyslogBeginMsgRgx;
    Regex acSyslogTimeRgx;
    Regex dateRgx;
    Regex srcIpRgx;   
    Regex dstIpRgx;
    Regex requestRgx;
    Regex callidRgx;
    Regex toRgx;
    Regex fromRgx;
    Regex uaRgx;
    Regex serverRgx;
    Regex portRgx;
    Regex codecRgx;
    Regex SDPIPRgx;
    Regex mAudioRgx;
    Regex occasRgx;
    Regex cseqRgx;
    static readonly object _DataLocker = new object();
    static readonly object _QueryAgainlocker = new object();
    static readonly object _DisplayLocker = new object();
    static readonly object _LogLocker = new object();
    enum CallLegColors { Green, Cyan, Red, Magenta, Yellow, DarkGreen, DarkCyan, DarkRed, DarkMagenta };
    enum AttrColor : short { Black, DarkBlue, DarkGreen, DarkCyan, DarkRed, DarkMagenta, Darkyellow, Gray, DarkGray, Blue, Green, Cyan, Red, Magenta, Yellow, White, }
    String[,] sortFields;   
    List<string> streamData = new List<string>();
    List<string[]> messages = new List<string[]>();
    //  index start of msg[0] 
    //  date and time stamp[1] 
    //  UTC[2]
    //  src IP[3]
    //  dst IP[4] 
    //  Request/Method line of SIP msg[5] 
    //  Call-ID[6]
    //  To:[7]  
    //  From:[8]
    //  index end of msg[9]
    //  color [10]
    //  SDP [11]
    //  filename [12]
    //  SDP IP [13]
    //  SDP port [14]
    //  SDP codec [15]
    //  useragent or server[16]
    //  CSeq [17]
    List<string[]> callLegs = new List<string[]>();
    //  date and time stamp [0]
    //  UTC [1]
    //  To: [2]
    //  From: [3]
    //  Call-ID [4]
    //  selected [5]
    //  src ip [6]
    //  dst ip [7]
    //  end time [8]
    //  method(invite,notify,registraion,supscription) [9]
    String[] filter;
    List<string[]> callLegsDisplayed = new List<string[]>(); // filtered call legs where [8] == filtered 
    List<string[]> selectedmessages = new List<string[]>();  // call legs  where [5] == selected 
    List<string> IPsOfIntrest = new List<string>();   // all the IP addresses from the selectedmessages
    List<string> callIDsOfIntrest = new List<string>(); // all the callIDs from the selectedmesages 
    int CallInvites;
    int notifications;
    int registrations;
    int subscriptions;
    int callLegsDisplayedCountPrev;
    int prevNumSelectdIPs;
    int prevNumSelectMsg;
    int IPprevNumSelectMsg;
    int numSelectedCalls;
    bool filterChange = false;    
    StreamReader splunkSIPmessageSR;
    long currentSplunkLoadProg;
    bool SplunkReadDone = false;
    AttrColor statusBarTxtClr;
    AttrColor statusBarBkgrdClr;
    AttrColor headerTxtClr;
    AttrColor headerBkgrdClr;
    ConsoleColor fieldConsoleTxtClr;
    ConsoleColor fieldConsoleBkgrdClr;
    ConsoleColor fieldConsoleTxtInvrtClr;
    ConsoleColor fieldConsoleBkgrdInvrtClr;
    ConsoleColor fieldConsoleSelectClr;
    ConsoleColor msgBoxTxt;
    ConsoleColor msgBoxBkgrd;
    AttrColor fieldAttrTxtClr;
    AttrColor fieldAttrTxtInvrtClr;
    AttrColor fieldAttrBkgrdClr;
    AttrColor fieldAttrBkgrdInvrtClr;
    AttrColor fieldAttrSelectClr;
    AttrColor footerTxtClr;
    AttrColor footerBkgrdClr;
    AttrColor sortTxtdClr;
    AttrColor sortBkgrdClr;
    int[] fakeCursor = new int[2];
    int flowWidth;
    string methodDisplayed;
    string displayMode;
    enum TZmode { local,utc,stamp };
    TZmode timeMode;
    bool showNotify;
    int CallListPosition;
    int callsDisplaysortIdx;
    bool dupIP;
    int flowSelectPosition;
    StreamWriter flowFileWriter;
    bool writeFlowToFile;
    bool htmlFlowToFile;
    string splunkUrl;
    string user;
    SecureString password;
    string searchStrg;
    string earliest;
    string latest;
    bool splunkExceptions;
    bool CancelSplunkJob;
    int splunkMaxEvents;
    int splunkMaxTime;
    int splunkStatusInterval;
    bool okToQuerySIPmsg;
    StreamWriter logFileSW;
    string logMode;
    DateTime SelectedCallsEarliestTime;
    DateTime SelectedCallsLatestTime;

    public SipSplunk()
    {
        Regex.CacheSize = 19;
        beginmsgRgx = new Regex(beginMsgRgxStr, RegexOptions.Compiled);
        acBeginMsgRgx = new Regex(acBeginMsgRgxStr, RegexOptions.Compiled);
        acSyslogBeginMsgRgx = new Regex(acSyslogBeginMsgRgxStr, RegexOptions.Compiled);
        acSyslogTimeRgx = new Regex(acSyslogTimeRgxStr, RegexOptions.Compiled);
        dateRgx = new Regex(dateRgxStr, RegexOptions.Compiled);        
        srcIpRgx = new Regex(srcIpRgxStr, RegexOptions.Compiled);        
        dstIpRgx = new Regex(dstIpRgxStr, RegexOptions.Compiled);
        requestRgx = new Regex(requestRgxStr, RegexOptions.Compiled);
        callidRgx = new Regex(callidRgxStr, RegexOptions.Compiled);
        toRgx = new Regex(toRgxStr, RegexOptions.Compiled);
        fromRgx = new Regex(fromRgxStr, RegexOptions.Compiled);
        uaRgx = new Regex(uaRgxStr, RegexOptions.Compiled);
        serverRgx = new Regex(serverRgxStr, RegexOptions.Compiled);
        portRgx = new Regex(portRgxStr, RegexOptions.Compiled);
        codecRgx = new Regex(codecRgxStr, RegexOptions.Compiled);
        SDPIPRgx = new Regex(SDPIPRgxStr, RegexOptions.Compiled);
        mAudioRgx = new Regex(mAudioRgxStr, RegexOptions.Compiled);
        occasRgx = new Regex(occasRgxStr, RegexOptions.Compiled);
        cseqRgx = new Regex(cseqRgxStr, RegexOptions.Compiled);
        statusBarTxtClr = AttrColor.White;
        statusBarBkgrdClr = AttrColor.Black;
        headerTxtClr = AttrColor.Green;
        headerBkgrdClr = AttrColor.DarkBlue;
        fieldConsoleTxtClr = ConsoleColor.Gray;
        fieldConsoleBkgrdClr = ConsoleColor.DarkBlue;
        fieldConsoleSelectClr = ConsoleColor.Yellow;
        fieldConsoleTxtInvrtClr = ConsoleColor.DarkBlue;
        fieldConsoleBkgrdInvrtClr = ConsoleColor.Gray;
        msgBoxTxt = ConsoleColor.White;
        msgBoxBkgrd = ConsoleColor.DarkGray;
        fieldAttrTxtClr = AttrColor.Gray;
        fieldAttrTxtInvrtClr = AttrColor.DarkBlue;
        fieldAttrBkgrdClr = AttrColor.DarkBlue;
        fieldAttrBkgrdInvrtClr = AttrColor.Gray;
        fieldAttrSelectClr = AttrColor.Yellow;
        footerTxtClr = AttrColor.Cyan;
        footerBkgrdClr = AttrColor.DarkBlue;
        sortTxtdClr = AttrColor.DarkBlue;
        sortBkgrdClr = AttrColor.Green;
        fakeCursor[0] = 0;
        fakeCursor[1] = 0;        
        showNotify = false;
        methodDisplayed = "INVITE";
        dupIP = false;
        IPprevNumSelectMsg = 0;
        numSelectedCalls = 0;
        sortFields = new string[5, 3]
        {
            { "time", "21", "0"} ,
            { "from:", "34", "3"} ,
            { "to:", "65", "2"} ,
            { "src IP", "96", "6"} ,
            { "dst IP", "113", "7"}
        };
        callsDisplaysortIdx = 0;
        filter = new String[20];
        writeFlowToFile = false;
        htmlFlowToFile = false;
        splunkExceptions = false;
        timeMode = TZmode.local;
        password = new SecureString();
        CancelSplunkJob = false;
        splunkMaxEvents = 0;
        splunkMaxTime = 0;
        splunkStatusInterval = 5000;
        logFileSW = File.AppendText("log.txt");
        logMode = "";
    }

    static void Main(String[] arg)
    {
        try
        {
            string version = "1.5.1";
            string dotNetVersion = Environment.Version.ToString();
            if (Console.BufferWidth < 200) { Console.BufferWidth = 200; }
            Console.Clear();
            Console.SetCursorPosition(0, 0); Console.WriteLine();

            Console.WriteLine(@"   _____ _____ _____  _                         _             _        ");
            Console.WriteLine(@"  / ____|_   _|  __ \| |                       | |           | |       ");
            Console.WriteLine(@" | (___   | | | |__) | | ___   __ _   ___ _ __ | |_   _ _ __ | | __    ");
            Console.WriteLine(@"  \___ \  | | |  ___/| |/ _ \ / _` | / __| '_ \| | | | | '_ \| |/ /    ");
            Console.WriteLine(@"  ____) |_| |_| |    | | (_) | (_| | \__ \ |_) | | |_| | | | |   <     ");
            Console.WriteLine(@" |_____/|_____|_|    |_|\___/ \__, | |___/ .__/|_|\__,_|_| |_|_|\_\    ");
            Console.WriteLine(@"                              __ / |     | |                           ");
            Console.WriteLine(@"                             | ___/      |_|                           ");
            Console.WriteLine("                                              Version {0} Greg Palmer   ", version);
            Console.WriteLine();
            Console.WriteLine();

            //check if there is .NET
            if (!Regex.IsMatch(dotNetVersion, @"^4\.")){
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(@"SIPlog requires .NET 4 runtime https://www.microsoft.com/net/download/windows");
                Console.ForegroundColor = ConsoleColor.Gray;
                Environment.Exit(1);
            }

            //load .settings file for splunk limts
            SipSplunk SipSplunkObj = new SipSplunk();            
            if (File.Exists("siplogsplunk.settings")){
                string[] settingsFile = File.ReadAllLines("siplogsplunk.settings");
                foreach (string line in settingsFile){
                    if (Regex.IsMatch(line, @"splunkMaxEvents\s*=\s*\d*")){
                        Console.WriteLine(line);
                        SipSplunkObj.splunkMaxEvents = int.Parse(Regex.Match(line, @"(splunkMaxEvents)(\s*=\s*)(\d*)").Groups[3].ToString());                                               
                    }
                    if (Regex.IsMatch(line, @"splunkMaxTime\s*=\s*\d*")){
                        Console.WriteLine(line);
                        SipSplunkObj.splunkMaxTime = int.Parse(Regex.Match(line, @"(splunkMaxTime)(\s*=\s*)(\d*)").Groups[3].ToString());
                    }
                    if (Regex.IsMatch(line, @"splunkStatusInterval\s*=\s*\d*")){
                        Console.WriteLine(line);
                        SipSplunkObj.splunkStatusInterval = int.Parse(Regex.Match(line, @"(splunkStatusInterval)(\s*=\s*)(\d*)").Groups[3].ToString());
                    }
                }
            }
            else{
                Console.WriteLine("siplogsplunk.settings is missing");
                Environment.Exit(1);
            }
            if (SipSplunkObj.splunkMaxEvents == 0 || SipSplunkObj.splunkMaxTime == 0 || SipSplunkObj.splunkStatusInterval == 0){
                Console.WriteLine("a setting from siplogsplunk.settings is missing");
                Environment.Exit(1);
            }
            Console.WriteLine();
            
            //load config file for Splunk server, search string and time frame
            if (arg.Length > 0){
                try{
                    string[] configFileLines = File.ReadAllLines(arg[0]);
                    SipSplunkObj.splunkUrl = configFileLines[0].Trim();
                    SipSplunkObj.searchStrg = configFileLines[1].Trim();
                    SipSplunkObj.earliest = configFileLines[2].Trim();
                    SipSplunkObj.latest = configFileLines[3].Trim();
                    SipSplunkObj.logMode = configFileLines[4].Trim();
                }
                catch (Exception ex){
                    Console.WriteLine(ex.ToString().Substring(0, ex.ToString().IndexOf(Environment.NewLine)));
                    Console.ReadKey(true);
                    Environment.Exit(1);
                }
            }

            /*
            SipSplunkObj.splunkUrl = "https://10.204.140.100:8089/";
            SipSplunkObj.user = "admin";
            SipSplunkObj.searchStrg = "index=siplog";
            //SipSplunkObj.earliest = "2018-01-19T00:00:00.000-05:00";
            //SipSplunkObj.latest = "2018-01-20T00:00:00.000-05:00";
            SipSplunkObj.earliest = "-131 days";
            SipSplunkObj.latest = "-130 days";
            */
            
            //test if the loaded info is correct and if not or is missing prompt for it
            Regex earliestTimeAndDateRGX = new Regex(@"\d{4}-\d{2}-\d{1,2}T\d{2}:\d{2}:\d{2}.\d{3}(-|\+)\d{2}:\d{2}|-\d{1,3}\s*(s|m|h|d|w|M|q|y)", RegexOptions.IgnoreCase);
            Regex latestTimeAndDateRGX = new Regex(@"\d{4}-\d{2}-\d{1,2}T\d{2}:\d{2}:\d{2}.\d{3}(-|\+)\d{2}:\d{2}|-\d{1,3}\s*(s|m|h|d|w|M|q|y)|now", RegexOptions.IgnoreCase);
            bool goodentry = false;
            if (String.IsNullOrEmpty(SipSplunkObj.splunkUrl) || !SipSplunkObj.splunkUrl.StartsWith("https://") || !Uri.IsWellFormedUriString(SipSplunkObj.splunkUrl, UriKind.RelativeOrAbsolute)){
                while (!goodentry){
                    Console.Write("Enter Splunk API URL ex. https://10.0.0.1:8089/ : ");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("https://");
                    Console.CursorLeft -= 8;
                    Console.ForegroundColor = ConsoleColor.Gray;
                    SipSplunkObj.splunkUrl = Console.ReadLine();
                    if (!String.IsNullOrEmpty(SipSplunkObj.splunkUrl) && SipSplunkObj.splunkUrl.StartsWith("https://") && Uri.IsWellFormedUriString(SipSplunkObj.splunkUrl, UriKind.RelativeOrAbsolute)) { goodentry = true; }
                }
            }
            goodentry = false;
            if (String.IsNullOrEmpty(SipSplunkObj.user)){
                while (!goodentry){
                    Console.Write("Enter Splunk user for " + SipSplunkObj.splunkUrl + " : ");
                    SipSplunkObj.user = Console.ReadLine();
                    if (!String.IsNullOrEmpty(SipSplunkObj.user)) { goodentry = true; }
                }
            }
            goodentry = false;
            while (!goodentry){
                Console.Write("Enter Splunk user " + SipSplunkObj.user + " password : ");
                ConsoleKeyInfo key;            
                do{
                    key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Backspace){
                        if (SipSplunkObj.password.Length > 0){
                            SipSplunkObj.password.RemoveAt(SipSplunkObj.password.Length - 1);
                            Console.Write("\b \b");
                        }
                    }
                    if (((decimal)key.KeyChar) >= 32 && ((decimal)key.KeyChar <= 126)){
                        SipSplunkObj.password.AppendChar(key.KeyChar);
                        Console.Write("*");
                    }                
                } while (key.Key != ConsoleKey.Enter);
                Console.WriteLine();
                if (SipSplunkObj.password.Length > 0) goodentry = true;
            }
            goodentry = false;
            if (String.IsNullOrEmpty(SipSplunkObj.searchStrg) || !SipSplunkObj.searchStrg.Contains("index=")){
                while (!goodentry){
                    Console.WriteLine("Enter Splunk application and search string. Must contain \"index=\"");
                    Console.Write("example: search index=siplogs 2035551212 : ");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("search index=");
                    Console.CursorLeft -= 13;
                    Console.ForegroundColor = ConsoleColor.Gray;
                    SipSplunkObj.searchStrg = Console.ReadLine();
                    if (SipSplunkObj.searchStrg.Contains("index=")) { goodentry = true; }
                }
            }
            goodentry = false;
            if (String.IsNullOrEmpty(SipSplunkObj.logMode) || !(SipSplunkObj.logMode.Contains("tcpdump") || SipSplunkObj.logMode.Contains("audiocodes") || SipSplunkObj.logMode.Contains("audiocodesSyslog")))
            {
                while (!goodentry)
                {
                    Console.Write("Enter the log type tcpdump, audiocodes or audiocodesSyslog : ");
                    SipSplunkObj.logMode = Console.ReadLine();
                    if (SipSplunkObj.logMode.Contains("tcpdump") || SipSplunkObj.logMode.Contains("audiocodes") || SipSplunkObj.logMode.Contains("audiocodesSyslog")) { goodentry = true; }
                }
            }
            goodentry = false;
            bool goodTimeEntry = false;
            while (!goodTimeEntry){
                if (String.IsNullOrEmpty(SipSplunkObj.earliest) || !earliestTimeAndDateRGX.IsMatch(SipSplunkObj.earliest)){
                    while (!goodentry){
                    Console.WriteLine("Enter search begining time in format 2018-02-6T06:00:00.000-05:00 or");
                    Console.Write("relative -2 days or -5h. (s,m,h,d,w,mon,q,y) : ");
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("YYYY-MM-DDTHH:mm:ss.sss+hh:mm");
                        Console.CursorLeft -= 29;
                        Console.ForegroundColor = ConsoleColor.Gray;
                        SipSplunkObj.earliest = Console.ReadLine();
                    if (earliestTimeAndDateRGX.IsMatch(SipSplunkObj.earliest)) { goodentry = true; }
                    }
                }
                goodentry = false;
                if (String.IsNullOrEmpty(SipSplunkObj.latest) || !latestTimeAndDateRGX.IsMatch(SipSplunkObj.latest)){
                    while (!goodentry){
                        Console.WriteLine("Enter search end time in format 2018-02-6T06:00:00.000-05:00,");
                        Console.Write("relative(s,m,h,d,w,mon,q,y) or now : ");
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("YYYY-MM-DDTHH:mm:ss.sss+hh:mm");
                        Console.CursorLeft -= 29;
                        Console.ForegroundColor = ConsoleColor.Gray;
                        SipSplunkObj.latest = Console.ReadLine();
                    if (latestTimeAndDateRGX.IsMatch(SipSplunkObj.latest)) { goodentry = true; }
                    }
                }
                if (Regex.IsMatch(SipSplunkObj.latest, @"\d{4}-\d{2}-\d{1,2}T\d{2}:\d{2}:\d{2}.\d{3}-\d{2}:\d{2}") && Regex.IsMatch(SipSplunkObj.earliest, @"\d{4}-\d{2}-\d{1,2}T\d{2}:\d{2}:\d{2}.\d{3}-\d{2}:\d{2}")){
                    if (DateTime.Parse(SipSplunkObj.earliest) > DateTime.Parse(SipSplunkObj.latest)){
                        Console.Write("start time is later than end time");
                        SipSplunkObj.earliest = "";
                        SipSplunkObj.latest = "";
                    }
                    else{
                        goodTimeEntry = true;
                    }
                }
                else{
                    goodTimeEntry = true;
                }
            }
            
            //set screen mode to call list
            SipSplunkObj.displayMode = "calls";
            
            //start Splunk Query and Data reader thread
            Thread SplunkReadThread = new Thread(() => { SipSplunkObj.SplunkGetCallLegs(); });
            SplunkReadThread.Name = "Splunk Query/Reader Thread";
            SplunkReadThread.Start();
            
            //start GUI thread
            SipSplunkObj.CallSelect(); 
        }
        catch (Exception ex)
        {
            lock (_LogLocker)
            {
                StreamWriter logFileSW = File.AppendText("log.txt");
                Log(ex.ToString(), logFileSW);
            }
        }
    }    

    void SplunkGetCallLegs()
    {
        okToQuerySIPmsg = false;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => {
            return true;
        };        

        //loop indefinately and wait for pulse from GUI thread to query again
        while (true){
            SelectedCallsEarliestTime = DateTime.Now;
            SelectedCallsLatestTime = DateTime.Parse("2000-01-01T00:00:00.000-05:00");
            splunkExceptions = false;
            using (Service service = new Service(new Uri(splunkUrl))) {

                //login to splunk server and call SplunkQuery
                try{
                    SplunkReadDone = false;
                    TopLine("Connecting to splunk", 0);
                    service.LogOnAsync(user, SecureStringToString(password)).Wait();
                    TopLine("Creating splunk job " + searchStrg, 0);
                    switch (logMode)
                    {
                        case "tcpdump":
                            SplunkCallLegsQuery(service).Wait();
                            break;
                        case "audiocodes":
                            AcSplunkCallLegsQuery(service).Wait();
                            break;
                        case "audiocodesSyslog":
                            AcSyslogSplunkCallLegsQuery(service).Wait();
                            break;
                    }                                        
                    SplunkReadDone = true;
                }
                catch (AggregateException ex){
                   
                    //if the wrong splunk URL
                    if (ex.ToString().Contains("System.Net.Sockets.SocketException"))
                    {
                        TopLine(Regex.Match(ex.InnerException.ToString(), @"(?<=System.Net.Sockets.SocketException:).*").ToString(), 0);
                    }
                    //if the wrong user or password
                    else if (ex.ToString().Contains("Splunk.Client.AuthenticationFailureException"))
                    {
                        TopLine(Regex.Match(ex.ToString(), @"(?<=Splunk.Client.AuthenticationFailureException).*").ToString(), 0);
                    }
                    else if (ex.InnerException.Message.Contains("Unknown search command"))
                    {
                        TopLine(Regex.Match(ex.InnerException.Message, @"(?<=Search Factory: ).*\s*").ToString(), 0);                        
                    }
                    else if (ex.ToString().Contains("System.Net.WebException:"))
                    {
                        TopLine(Regex.Match(ex.ToString(), @"(?<=System.Net.WebException: ).*\s*").ToString(), 0);
                    }
                    else
                    {
                        TopLine(ex.ToString(),0);
                        
                    }
                    Log(ex.ToString(), logFileSW);
                    splunkExceptions = true;
                    SplunkReadDone = true;
                }
                finally
                {
                    try
                    {
                        if (!splunkExceptions) service.LogOffAsync().Wait();
                    }
                    catch (Exception ex)
                    {
                        Log(ex.ToString(), logFileSW);
                    }
                    okToQuerySIPmsg = true;
                }
                lock (_QueryAgainlocker)
                {
                    Monitor.Wait(_QueryAgainlocker);
                }
                CancelSplunkJob = false;
            }
        }
    }

    async Task SplunkCallLegsQuery(Service service)
    {
        try
        {
            string front = searchStrg +" |";
            string splunkSrcIpPortRgxStr = @"(?<SIP_SrcIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})(:|.)\d*(?= >)";
            string splunkDstIpPortRgxStr = @"(?<SIP_DstIP>(?<=> )\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})(:|.)\d*";
            string splunkRequestRgxStr = @"(?<SIP_Req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))";
            string splunkCallidRgxStr = @"(?<!-.{8})(?<=Call-ID:)\s*(?<SIP_CallId>\S*)";//do not match if -Call-ID instead of Call-ID
            string splunkToRgxStr = @"(?<=To:)\s*(\x22.+\x22)?.*<?(sip:)(?<SIP_To>[^@>]+)";
            string splunkFromRgxStr = @"(?<=From:)\s*(\x22.+\x22)?.*<?(sip:)(?<SIP_From>[^@>]+)";
            string splunkMethodRgxStr = @"(?<SIP_method>^[a-zA-Z]+)";
            string splunkMethodRex = "rex field=SIP_Req \"";
            string back = "eval timeForamted=strftime(_time, \"%Y-%m-%d %H:%M:%S.%6N%:z\")|search SIP_Req = *INVITE* OR SIP_Req =*NOTIFY* OR SIP_Req =*REGISTER* OR SIP_Req =*SUBSCRIBE*| reverse |stats first(SIP_To) as To, first(SIP_From) as From, first(SIP_SrcIP) as Source_IP, first(SIP_DstIP) as Destination_IP, first(timeForamted)  as DateTime last(timeForamted) as endDateTime first(SIP_method) as Method by SIP_CallId| table DateTime, UTC, To, From, SIP_CallId, selected, Source_IP, Destination_IP, endDateTime, Method | sort DateTime";
            string rex = "rex field=_raw \"";
            string rexend = "\"|";            
            var splunkJob = await service.Jobs.CreateAsync(
                front + 
                rex + splunkSrcIpPortRgxStr + rexend + 
                rex + splunkDstIpPortRgxStr + rexend + 
                rex + splunkRequestRgxStr + rexend + 
                rex + splunkCallidRgxStr + rexend + 
                rex + splunkToRgxStr + rexend + 
                rex + splunkFromRgxStr + rexend +
                splunkMethodRex + splunkMethodRgxStr + rexend +
                back, splunkMaxEvents, ExecutionMode.Normal,
                new JobArgs()
                {
                    EarliestTime = earliest,
                    LatestTime = latest,
                    MaxCount = splunkMaxEvents
                });

            //loop until Job is done or cancelled 
            Stopwatch elapsedTime = new Stopwatch();
            elapsedTime.Start();
            for (int count = 1; ; ++count)
            {
                if (CancelSplunkJob)
                {
                    await splunkJob.CancelAsync();
                    TopLine("Splunk query is canceled.", 0);
                    break;
                }
                if (count >= splunkMaxTime / splunkStatusInterval)
                {
                    await splunkJob.FinalizeAsync();
                    TopLine("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", 0);
                    break;
                }
                if (splunkJob.IsFinalized)
                {
                    TopLine("Splunk query is finalized", 0);
                    break;
                }
                if (splunkJob.DispatchState == DispatchState.Finalizing)
                {
                    string formatedString = String.Format("Splunk job " + splunkJob.Sid + " Finalizing. Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                }
                try
                {
                    await splunkJob.TransitionAsync(DispatchState.Done, splunkStatusInterval);
                    break;
                }
                catch (TaskCanceledException)
                {
                    string formatedString = String.Format("Waiting on splunk job " + splunkJob.Sid + " to complete. "+ splunkJob.DoneProgress*100 +"% Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                    TopLine(formatedString, 0);
                }
            }
            elapsedTime.Restart();
            using (var results = await splunkJob.GetSearchResponseMessageAsync(outputMode: OutputMode.Csv))
            {
                Stream contentstream = await results.Content.ReadAsStreamAsync();
                StreamReader contentSR = new StreamReader(contentstream);
                //Console.WriteLine(content);
                String[] line = new String[5];
                long lastElapsedMs = elapsedTime.ElapsedMilliseconds;
                while (!contentSR.EndOfStream && !CancelSplunkJob)
                {
                   if ((elapsedTime.ElapsedMilliseconds - lastElapsedMs) > 5000)
                   {
                        lastElapsedMs = elapsedTime.ElapsedMilliseconds;
                        string formatedString = String.Format("Fetching results from splunk job " + splunkJob.ResultCount + " results. Time elapsed: {0:hh\\:mm\\:ss}", elapsedTime.Elapsed);
                        TopLine(formatedString, 0);
                   }
                    line = contentSR.ReadLine().Replace("\"", "").Split(',');

                    //if line has a valid time stamp collect it
                    if (Regex.IsMatch(line[0], @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{6}-\d{2}:\d{2}"))
                    {
                        line[1]=DateTime.Parse(line[0]).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                        callLegs.Add(line);                        
                    }
                }
                elapsedTime.Stop();
                TopLine("Completed splunk query with "+splunkJob.ResultCount + " results out of " + splunkJob.EventCount + " Events found", 0);
                lock (_DisplayLocker) if (displayMode == "calls")
                { // displayMode CallFilter methodDisplayed showNotify CallDisplay() touched by another thread                    
                    CallFilter();
                    CallDisplay(true);                        
                }
            }            
        }        
        catch (Exception ex ){
            if (ex.ToString().Contains("System.Net.WebException:"))
            {
                TopLine(Regex.Match(ex.ToString(), @"(?<=System.Net.WebException: ).*\s*").ToString(), 0);
            }
            else
            {
                TopLine(ex.Message, 0);
            }
            Log(ex.ToString(), logFileSW);
            splunkExceptions = true;
        }
    }

    async Task AcSplunkCallLegsQuery(Service service)
    {
        try
        {
            string query = searchStrg + " | rex field=_raw \"" + @"(?<SIP_Req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))" + "\" | " +
                            "rex field=_raw \"" + @"(?<!-.{8})(?<=Call-ID:)\s*(?<SIP_CallId>\S*)" + "\" | " +
                            "rex field=SIP_Req \"(?<SIP_method>^[a-zA-Z]+)\" | " +
                            "rex field=_raw \"\\[.*\\]\\s*\\[.*\\]\\s*(?<MGIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                            "rex field=_raw \"(?<=Incoming SIP Message from)\\s*(?<SrcIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                            "rex field=_raw \"(?<=Outgoing SIP Message to)\\s*(?<DstIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                            "rex field=_raw \"" + @"(?<=To:) *(\x22.+\x22)? *<?(sip:)(?<SIP_To>[^@>]+)" + "\" | " +
                            "rex field=_raw \"" + @"(?<=From:) *(\x22.+\x22)? *<?(sip:)(?<SIP_From>[^@>]+)" + "\" | " +
                            "eval timeForamted = strftime(_time, \"%Y-%m-%d %H:%M:%S.%6N%:z\") |" +
                            "eval UTC = \"\" |" +
                            "eval selected = \"\" |" +
                            "eval filtered = \"\" |" +
                            "reverse | streamstats current=f window=5 last(DstIP) as prev_DstIP last(SrcIP) as prev_SrcIP |" +
                            "eval SIP_dstIP =if (prev_DstIP != \"\",prev_DstIP,MGIP) | eval SIP_srcIP =if (prev_SrcIP != \"\",prev_SrcIP,MGIP) |" +
                            "search SIP_Req = *INVITE* OR SIP_Req = *NOTIFY* OR SIP_Req = *REGISTER* OR SIP_Req = *SUBSCRIBE* |" +
                            "stats first(SIP_To) as To, first(SIP_From) as From, first(SIP_srcIP) as Source_IP, first(SIP_dstIP) as Destination_IP, first(timeForamted) as DateTime first(SIP_method) as Method by SIP_CallId|" +
                            "table DateTime,UTC,To,From,SIP_CallId,selected,Source_IP,Destination_IP,filtered,Method |" +
                            "sort DateTime";            
           
            var splunkJob = await service.Jobs.CreateAsync(query, splunkMaxEvents, ExecutionMode.Normal, new JobArgs()
                {
                    EarliestTime = earliest,
                    LatestTime = latest,
                    MaxCount = splunkMaxEvents
                });

            //loop until Job is done or cancelled 
            Stopwatch elapsedTime = new Stopwatch();
            elapsedTime.Start();
            for (int count = 1; ; ++count)
            {
                if (CancelSplunkJob)
                {
                    await splunkJob.CancelAsync();
                    TopLine("Splunk query is canceled.", 0);
                    break;
                }
                if (count >= splunkMaxTime / splunkStatusInterval)
                {
                    await splunkJob.FinalizeAsync();
                    TopLine("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", 0);
                    break;
                }
                if (splunkJob.IsFinalized)
                {
                    TopLine("Splunk query is finalized", 0);
                    break;
                }
                if (splunkJob.DispatchState == DispatchState.Finalizing)
                {
                    string formatedString = String.Format("Splunk job " + splunkJob.Sid + " Finalizing. Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                }
                try
                {
                    await splunkJob.TransitionAsync(DispatchState.Done, splunkStatusInterval);
                    break;
                }
                catch (TaskCanceledException)
                {
                    string formatedString = String.Format("Waiting on splunk job " + splunkJob.Sid + " to complete. " + splunkJob.DoneProgress * 100 + "% Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                    TopLine(formatedString, 0);
                }
            }
            elapsedTime.Restart();
            using (var results = await splunkJob.GetSearchResponseMessageAsync(outputMode: OutputMode.Csv))
            {
                Stream contentstream = await results.Content.ReadAsStreamAsync();
                StreamReader contentSR = new StreamReader(contentstream);
                //Console.WriteLine(content);
                String[] line = new String[5];
                long lastElapsedMs = elapsedTime.ElapsedMilliseconds;
                while (!contentSR.EndOfStream && !CancelSplunkJob)
                {
                    if ((elapsedTime.ElapsedMilliseconds - lastElapsedMs) > 5000)
                    {
                        lastElapsedMs = elapsedTime.ElapsedMilliseconds;
                        string formatedString = String.Format("Fetching results from splunk job " + splunkJob.ResultCount + " results. Time elapsed: {0:hh\\:mm\\:ss}", elapsedTime.Elapsed);
                        TopLine(formatedString, 0);
                    }
                    line = contentSR.ReadLine().Replace("\"", "").Split(',');

                    //if line has a valid time stamp collect it
                    if (Regex.IsMatch(line[0], @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{6}-\d{2}:\d{2}"))
                    {
                        line[1] = DateTime.Parse(line[0]).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                        callLegs.Add(line);
                    }
                }
                elapsedTime.Stop();
                TopLine("Completed splunk query with " + splunkJob.ResultCount + " results out of " + splunkJob.EventCount + " Events found", 0);
                lock (_DisplayLocker) if (displayMode == "calls")
                    { // displayMode CallFilter methodDisplayed showNotify CallDisplay() touched by another thread                    
                        CallFilter();
                        CallDisplay(true);
                    }
            }
        }
        catch (AggregateException ex)
        {

            TopLine(ex.Message, 0);
            Log(ex.ToString(), logFileSW);
            splunkExceptions = true;
        }
    }

    async Task AcSyslogSplunkCallLegsQuery(Service service)
    {
        try
        {
            string query = searchStrg + " | rex field=_raw \"" + @"(?<SIP_Req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))" + "\" | " +
                            "rex field=_raw \"" + @"(?<!-.{8})(?<=Call-ID:)\s*(?<SIP_CallId>\S*)" + "\" | " +
                            "rex field=SIP_Req \"(?<SIP_method>^[a-zA-Z]+)\" | " +
                            "rex field=_raw \"" + @"\d{2}:\d{2}:\d{2}.\d{3}\s*(?<MGIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})" + "\" | " +
                            "rex field=_raw \"(?<=Incoming SIP Message from)\\s*(?<SrcIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                            "rex field=_raw \"(?<=Outgoing SIP Message to)\\s*(?<DstIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                            "rex field=_raw \"" + @"(?<=To:) *(\x22.+\x22)? *<?(sip:)(?<SIP_To>[^@>]+)" + "\" | " +
                            "rex field=_raw \"" + @"(?<=From:) *(\x22.+\x22)? *<?(sip:)(?<SIP_From>[^@>]+)" + "\" | " +
                            "eval timeForamted = strftime(_time, \"%Y-%m-%d %H:%M:%S.%6N%:z\") |" +
                            "eval UTC = \"\" |" +
                            "eval selected = \"\" |" +
                            "eval filtered = \"\" |" +
                            "reverse | streamstats current=f window=5 last(DstIP) as prev_DstIP last(SrcIP) as prev_SrcIP |" +
                            "eval SIP_dstIP =if (prev_DstIP != \"\",prev_DstIP,MGIP) | eval SIP_srcIP =if (prev_SrcIP != \"\",prev_SrcIP,MGIP) |" +
                            "search SIP_Req = *INVITE* OR SIP_Req = *NOTIFY* OR SIP_Req = *REGISTER* OR SIP_Req = *SUBSCRIBE* |" +
                            "stats first(SIP_To) as To, first(SIP_From) as From, first(SIP_srcIP) as Source_IP, first(SIP_dstIP) as Destination_IP, first(timeForamted) as DateTime first(SIP_method) as Method by SIP_CallId|" +
                            "table DateTime,UTC,To,From,SIP_CallId,selected,Source_IP,Destination_IP,filtered,Method |" +
                            "sort DateTime";

            var splunkJob = await service.Jobs.CreateAsync(query, splunkMaxEvents, ExecutionMode.Normal, new JobArgs()
            {
                EarliestTime = earliest,
                LatestTime = latest,
                MaxCount = splunkMaxEvents
            });

            //loop until Job is done or cancelled 
            Stopwatch elapsedTime = new Stopwatch();
            elapsedTime.Start();
            for (int count = 1; ; ++count)
            {
                if (CancelSplunkJob)
                {
                    await splunkJob.CancelAsync();
                    TopLine("Splunk query is canceled.", 0);
                    break;
                }
                if (count >= splunkMaxTime / splunkStatusInterval)
                {
                    await splunkJob.FinalizeAsync();
                    TopLine("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", 0);
                    break;
                }
                if (splunkJob.IsFinalized)
                {
                    TopLine("Splunk query is finalized", 0);
                    break;
                }
                if (splunkJob.DispatchState == DispatchState.Finalizing)
                {
                    string formatedString = String.Format("Splunk job " + splunkJob.Sid + " Finalizing. Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                }
                try
                {
                    await splunkJob.TransitionAsync(DispatchState.Done, splunkStatusInterval);
                    break;
                }
                catch (TaskCanceledException)
                {
                    string formatedString = String.Format("Waiting on splunk job " + splunkJob.Sid + " to complete. " + splunkJob.DoneProgress * 100 + "% Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                    TopLine(formatedString, 0);
                }
            }
            elapsedTime.Restart();
            using (var results = await splunkJob.GetSearchResponseMessageAsync(outputMode: OutputMode.Csv))
            {
                Stream contentstream = await results.Content.ReadAsStreamAsync();
                StreamReader contentSR = new StreamReader(contentstream);
                //Console.WriteLine(content);
                String[] line = new String[5];
                long lastElapsedMs = elapsedTime.ElapsedMilliseconds;
                while (!contentSR.EndOfStream && !CancelSplunkJob)
                {
                    if ((elapsedTime.ElapsedMilliseconds - lastElapsedMs) > 5000)
                    {
                        lastElapsedMs = elapsedTime.ElapsedMilliseconds;
                        string formatedString = String.Format("Fetching results from splunk job " + splunkJob.ResultCount + " results. Time elapsed: {0:hh\\:mm\\:ss}", elapsedTime.Elapsed);
                        TopLine(formatedString, 0);
                    }
                    line = contentSR.ReadLine().Replace("\"", "").Split(',');

                    //if line has a valid time stamp collect it
                    if (Regex.IsMatch(line[0], @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{6}-\d{2}:\d{2}"))
                    {
                        line[1] = DateTime.Parse(line[0]).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                        callLegs.Add(line);
                    }
                }
                elapsedTime.Stop();
                TopLine("Completed splunk query with " + splunkJob.ResultCount + " results out of " + splunkJob.EventCount + " Events found", 0);
                lock (_DisplayLocker) if (displayMode == "calls")
                    { // displayMode CallFilter methodDisplayed showNotify CallDisplay() touched by another thread                    
                        CallFilter();
                        CallDisplay(true);
                    }
            }
        }
        catch (AggregateException ex)
        {

            TopLine(ex.Message, 0);
            Log(ex.ToString(), logFileSW);
            splunkExceptions = true;
        }
    }

    void SplunkGetSIPMessages()
    {
        while (!okToQuerySIPmsg) { } //loop and wait for call leg query to close before opening new query
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.ServerCertificateValidationCallback += (sender, certificate, chain, sslPolicyErrors) => {
            return true;
        };      
        
        splunkExceptions = false;
        using (Service service = new Service(new Uri(splunkUrl)))
        {//login to splunk server and call SplunkQuery
            try
            {
                SplunkReadDone = false;
                TopLine("Connecting to splunk", 0);
                service.LogOnAsync(user, SecureStringToString(password)).Wait();
                TopLine("Creating splunk job  for SIP messages " + searchStrg, 0);
                switch (logMode)
                {
                    case "tcpdump":
                        SplunkSIPMessagesQuery(service).Wait();
                        break;
                    case "audiocodes":
                        AcSplunkSIPMessagesQuery(service).Wait();
                        break;
                    case "audiocodesSyslog":
                        AcSyslogSplunkSIPMessagesQuery(service).Wait();
                        break;
                }
            }
            catch (Exception ex)
            {
                //if the wrong splunk URL
                if (ex.ToString().Contains("System.Net.Sockets.SocketException"))
                {
                    TopLine(Regex.Match(ex.InnerException.ToString(), @"(?<=System.Net.Sockets.SocketException:).*").ToString(), 0);
                }
                //if the wrong user or password
                else if (ex.ToString().Contains("Splunk.Client.AuthenticationFailureException"))
                {
                    TopLine(Regex.Match(ex.ToString(), @"(?<=Splunk.Client.AuthenticationFailureException).*").ToString(), 0);
                }
                else if (ex.InnerException.Message.Contains("Unknown search command"))
                {
                    TopLine(Regex.Match(ex.InnerException.Message, @"(?<=Search Factory: ).*\s*").ToString(), 0);                    
                }
                else
                {
                    TopLine(ex.InnerException.Message, 0);
                    Log(ex.ToString(), logFileSW);
                }
                splunkExceptions = true;
                SplunkReadDone = true;
            }
            finally
            {
                try
                {
                    if (!splunkExceptions) service.LogOffAsync().Wait();
                }
                catch (Exception ex)
                {
                    Log(ex.ToString(), logFileSW);
                }
            }                
        }        
    }

    async Task SplunkSIPMessagesQuery(Service service)
    {
        string msgSearchString = searchStrg + "|rex field=_raw \"(?<!-.{8})(?<=Call-ID:)\\s*(?<SIP_CallId>\\S*)\"| search ";
        for (int i=0;i < callIDsOfIntrest.Count;i++)
        {
            string callId = callIDsOfIntrest[i];
            msgSearchString += ("SIP_CallId=" + callId);
            if(i < callIDsOfIntrest.Count-1)
            {
                msgSearchString += " OR ";
            }
        }
        
        // create splunk job
        try
        {
            var splunkJob = await service.Jobs.CreateAsync(msgSearchString + " | dedup _raw | reverse", 0, ExecutionMode.Normal,
            new JobArgs()
            {
                EarliestTime = SelectedCallsEarliestTime.ToString("O"),
                LatestTime = SelectedCallsLatestTime.ToString("O"),
                MaxCount = splunkMaxEvents
            });

            //loop until Job is done or cancelled
            Stopwatch elapsedTime = new Stopwatch();
            elapsedTime.Start();
            for (int count = 1; ; ++count)
            {
                if (Console.KeyAvailable)
                {
                    if (Console.ReadKey(true).Key == ConsoleKey.Escape)
                    {
                        break;
                    }
                }
                if (count >= splunkMaxTime / splunkStatusInterval)
                {
                    await splunkJob.FinalizeAsync();
                    TopLine("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", 0);
                    Log("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", logFileSW);
                    break;
                }
                if (splunkJob.IsFinalized)
                {
                    TopLine("Splunk query is finalized", 0);
                    Log("Splunk query is finalized", logFileSW);
                    break;
                }
                if (splunkJob.DispatchState == DispatchState.Finalizing)
                {
                    string formatedString = String.Format("Splunk job " + splunkJob.Sid + " Finalizing. Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                }
                try
                {
                    await splunkJob.TransitionAsync(DispatchState.Done, splunkStatusInterval);
                    break;
                }
                catch (TaskCanceledException)
                {
                    string formatedString = String.Format("Waiting on splunk job " + splunkJob.Sid + " to complete. " + splunkJob.DoneProgress * 100 + "% Time elapsed: {0:hh\\:mm\\:ss} Press Esc to quit.", elapsedTime.Elapsed);
                    TopLine(formatedString, 0);
                }
            }
            elapsedTime.Restart();
            //Get results of job as stream instantiate streamreader splunkSR to read it
            if (splunkJob.IsFinalized || splunkJob.IsDone)
            {
                streamData.Clear();
                messages.Clear();
                selectedmessages.Clear();
                using (var message = await splunkJob.GetSearchResponseMessageAsync(outputMode: OutputMode.Raw))
                {
                    Stream splunkStream = await message.Content.ReadAsStreamAsync();
                    splunkSIPmessageSR = new StreamReader(splunkStream);
                    currentSplunkLoadProg = 0;
                    while (!splunkSIPmessageSR.EndOfStream)
                    {
                        ReadData();
                    }
                    splunkSIPmessageSR.Close();
                }
                if (!splunkExceptions) TopLine("Completed splunk query with " + streamData.Count() + " lines of data", 0);
                SplunkReadDone = true;
            }
            else
            {
                TopLine("Splunk query failed", 0);
            }
        }
        catch (Exception ex)
        {
            Log(ex.ToString(), logFileSW);
            splunkExceptions = true;
        }
    }

    async Task AcSplunkSIPMessagesQuery(Service service)
    {
        string msgSearchString = searchStrg +
                "| rex field=_raw \"" + @"(?<SIP_Req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))" + "\" | " +
                "rex field=_raw \"" + @"(?<!-.{8})(?<=Call-ID:)\s*(?<SIP_CallId>\S*)" + "\" | " +
                "rex field=SIP_Req \"(?<SIP_method>^[a-zA-Z]+)\" | " +
                "rex field=_raw \"\\[.*\\]\\s*\\[.*\\]\\s*(?<MGIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                "rex field=_raw \"(?<=Incoming SIP Message from)\\s*(?<SrcIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                "rex field=_raw \"(?<=Outgoing SIP Message to)\\s*(?<DstIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                "reverse |streamstats current=f window=5 last(DstIP) as prev_DstIP last(SrcIP) as prev_SrcIP | " +
                "eval SIP_dstIP=if(prev_DstIP != \"\",prev_DstIP,MGIP) | eval SIP_srcIP=if(prev_SrcIP != \"\",prev_SrcIP,MGIP) | " +
                "search ";
        string msgSearchStringEnd = " | eval srcIpOut=\"srcip=\"+SIP_srcIP | eval dstIpOut=\"dstip=\"+SIP_dstIP |" +                
                "table srcIpOut,dstIpOut,_raw | ";
        for (int i = 0; i < callIDsOfIntrest.Count; i++)
        {
            string callId = callIDsOfIntrest[i];
            msgSearchString += ("SIP_CallId=" + callId);
            if (i < callIDsOfIntrest.Count - 1)
            {
                msgSearchString += " OR ";
            }
        }

        // create splunk job
        try
        {
            var splunkJob = await service.Jobs.CreateAsync(msgSearchString + msgSearchStringEnd, 0, ExecutionMode.Normal,
            new JobArgs()
            {
                EarliestTime = SelectedCallsEarliestTime.ToString("O"),
                LatestTime = SelectedCallsLatestTime.ToString("O"),
                MaxCount = splunkMaxEvents
            });

            //loop until Job is done or cancelled
            Stopwatch elapsedTime = new Stopwatch();
            elapsedTime.Start();
            for (int count = 1; ; ++count)
            {
                if (Console.KeyAvailable)
                {
                    if (Console.ReadKey(true).Key == ConsoleKey.Escape)
                    {
                        break;
                    }
                }
                if (count >= splunkMaxTime / splunkStatusInterval)
                {
                    await splunkJob.FinalizeAsync();
                    TopLine("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", 0);
                    Log("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", logFileSW);
                    break;
                }
                if (splunkJob.IsFinalized)
                {
                    TopLine("Splunk query is finalized", 0);
                    break;
                }
                if (splunkJob.DispatchState == DispatchState.Finalizing)
                {
                    string formatedString = String.Format("Splunk job " + splunkJob.Sid + " Finalizing. Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                }
                try
                {
                    await splunkJob.TransitionAsync(DispatchState.Done, splunkStatusInterval);
                    break;
                }
                catch (TaskCanceledException)
                {
                    string formatedString = String.Format("Waiting on splunk job " + splunkJob.Sid + " to complete. " + splunkJob.DoneProgress * 100 + "% Time elapsed: {0:hh\\:mm\\:ss} Press Esc to quit.", elapsedTime.Elapsed);
                    TopLine(formatedString, 0);
                }
            }
            elapsedTime.Restart();
            //Get results of job as stream instantiate streamreader splunkSR to read it
            if (splunkJob.IsFinalized || splunkJob.IsDone)
            {
                streamData.Clear();
                messages.Clear();
                selectedmessages.Clear();
                using (var message = await splunkJob.GetSearchResponseMessageAsync(outputMode: OutputMode.Csv))
                {
                    Stream splunkStream = await message.Content.ReadAsStreamAsync();
                    splunkSIPmessageSR = new StreamReader(splunkStream);
                    currentSplunkLoadProg = 0;
                    while (!splunkSIPmessageSR.EndOfStream)
                    {
                        AcReadData();
                    }
                    splunkSIPmessageSR.Close();
                }
                if (!splunkExceptions) TopLine("Completed splunk query with " + streamData.Count() + " lines of data", 0);
                SplunkReadDone = true;
            }
            else
            {
                TopLine("Splunk query failed", 0);
            }
        }
        catch (Exception ex)
        {
            Log(ex.ToString(), logFileSW);
            splunkExceptions = true;
        }
    }

    async Task AcSyslogSplunkSIPMessagesQuery(Service service)
    {
        string msgSearchString = searchStrg +
                "| rex field=_raw \"" + @"(?<SIP_Req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))" + "\" | " +
                "rex field=_raw \"" + @"(?<!-.{8})(?<=Call-ID:)\s*(?<SIP_CallId>\S*)" + "\" | " +
                "rex field=SIP_Req \"(?<SIP_method>^[a-zA-Z]+)\" | " +
                "rex field=_raw \""+ @"\d{2}:\d{2}:\d{2}.\d{3}\s*(?<MGIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})" + "\" | " +
                "rex field=_raw \"(?<=Incoming SIP Message from)\\s*(?<SrcIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                "rex field=_raw \"(?<=Outgoing SIP Message to)\\s*(?<DstIP>\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})\" | " +
                "reverse |streamstats current=f window=5 last(DstIP) as prev_DstIP last(SrcIP) as prev_SrcIP | " +
                "eval SIP_dstIP=if(prev_DstIP != \"\",prev_DstIP,MGIP) | eval SIP_srcIP=if(prev_SrcIP != \"\",prev_SrcIP,MGIP) | " +
                "search ";
        string msgSearchStringEnd = " | eval srcIpOut=\"srcip=\"+SIP_srcIP | eval dstIpOut=\"dstip=\"+SIP_dstIP |" +
                "table srcIpOut,dstIpOut,_raw | ";
                
        for (int i = 0; i < callIDsOfIntrest.Count; i++)
        {
            string callId = callIDsOfIntrest[i];
            msgSearchString += ("SIP_CallId=" + callId);
            if (i < callIDsOfIntrest.Count - 1)
            {
                msgSearchString += " OR ";
            }
        }

        // create splunk job
        try
        {
            
            var splunkJob = await service.Jobs.CreateAsync(msgSearchString + msgSearchStringEnd, 0, ExecutionMode.Normal,
            new JobArgs()
            {
                EarliestTime = SelectedCallsEarliestTime.ToString("O"),
                LatestTime = SelectedCallsLatestTime.ToString("O"),
                MaxCount = splunkMaxEvents
            });

            //loop until Job is done or cancelled
            Stopwatch elapsedTime = new Stopwatch();
            elapsedTime.Start();
            for (int count = 1; ; ++count)
            {
                if (Console.KeyAvailable)
                {
                    if (Console.ReadKey(true).Key == ConsoleKey.Escape)
                    {
                        break;
                    }
                }
                if (count >= splunkMaxTime / splunkStatusInterval)
                {
                    await splunkJob.FinalizeAsync();
                    TopLine("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", 0);
                    Log("Exceeded maximum wait time of " + splunkMaxTime / 1000 + " seconds. Finalizing...", logFileSW);
                    break;
                }
                if (splunkJob.IsFinalized)
                {
                    TopLine("Splunk query is finalized", 0);
                    break;
                }
                if (splunkJob.DispatchState == DispatchState.Finalizing)
                {
                    string formatedString = String.Format("Splunk job " + splunkJob.Sid + " Finalizing. Time elapsed: {0:hh\\:mm\\:ss} ", elapsedTime.Elapsed);
                }
                try
                {
                    await splunkJob.TransitionAsync(DispatchState.Done, splunkStatusInterval);
                    break;
                }
                catch (TaskCanceledException)
                {
                    string formatedString = String.Format("Waiting on splunk job " + splunkJob.Sid + " to complete. " + splunkJob.DoneProgress * 100 + "% Time elapsed: {0:hh\\:mm\\:ss} Press Esc to quit.", elapsedTime.Elapsed);
                    TopLine(formatedString, 0);
                }
            }
            elapsedTime.Restart();
            //Get results of job as stream instantiate streamreader splunkSR to read it
            if (splunkJob.IsFinalized || splunkJob.IsDone)
            {
                streamData.Clear();
                messages.Clear();
                selectedmessages.Clear();
                using (var message = await splunkJob.GetSearchResponseMessageAsync(outputMode: OutputMode.Csv))
                {
                    Stream splunkStream = await message.Content.ReadAsStreamAsync();
                    splunkSIPmessageSR = new StreamReader(splunkStream);
                    currentSplunkLoadProg = 0;
                    while (!splunkSIPmessageSR.EndOfStream)
                    {
                        AcSyslogReadData();
                    }
                    splunkSIPmessageSR.Close();
                }
                if (!splunkExceptions) TopLine("Completed splunk query with " + streamData.Count() + " lines of data", 0);
                SplunkReadDone = true;
            }
            else
            {
                TopLine("Splunk query failed", 0);
            }
        }
        catch (Exception ex)
        {
            Log(ex.ToString(), logFileSW);
            splunkExceptions = true;
        }
    }

    void ReadData(){
        
        string line = GetNextLine();
        if (line != null){
            while (!string.IsNullOrEmpty(line) && beginmsgRgx.IsMatch(line)){
                String[] outputarray = new String[18];
                
                // get the index of the start of the msg
                outputarray[0] = currentSplunkLoadProg.ToString();
                outputarray[1] = dateRgx.Match(line).ToString();
                outputarray[2] = DateTime.Parse(dateRgx.Match(line).ToString()).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                outputarray[3] = srcIpRgx.Match(line).ToString();                              //src IP                                                                        
                outputarray[4] = dstIpRgx.Match(line).ToString(); 
                line = GetNextLine();
                if (line == null) { break; }
                
                //check to match these only once. no need match a field if it is already found
                bool sipTwoDotOfound = false;
                Match sipTwoDotO;
                Match callid;
                Match cseq;
                Match to;
                Match from; ;
                Match SDPIP;
                Match ua;
                Match serv;
                
                //untill the begining of the next msg
                while (!beginmsgRgx.IsMatch(line))
                { //match line against regexs
                    switch (line){
                        case string s when (sipTwoDotO = requestRgx.Match(s)) != Match.Empty:
                            outputarray[5] = sipTwoDotO.ToString();
                            sipTwoDotOfound = true;
                            break;
                        case string s when (callid = callidRgx.Match(s)) != Match.Empty:
                            outputarray[6] = callid.Groups[1].ToString();
                            break;
                        case string s when (cseq = cseqRgx.Match(s)) != Match.Empty:
                            outputarray[17] = cseq.Groups[2].ToString();
                            break;
                        case string s when (to = toRgx.Match(s)) != Match.Empty:
                            outputarray[7] = to.Groups[1].ToString() + to.Groups[3].ToString();
                            break;
                        case string s when (from = fromRgx.Match(s)) != Match.Empty:
                            outputarray[8] = from.Groups[1].ToString() + from.Groups[3].ToString();
                            break;
                        case string s when s.Contains("Content-Type: application/sdp"):
                            outputarray[11] = " SDP";
                            break;
                        case string s when (SDPIP = SDPIPRgx.Match(s)) != Match.Empty:
                            outputarray[13] = SDPIP.ToString();
                            break;
                        case string s when mAudioRgx.IsMatch(s):
                            outputarray[14] = portRgx.Match(s).ToString().Trim();
                            outputarray[15] = codecRgx.Match(s).ToString().Trim();
                            if (outputarray[15] == "0") { outputarray[15] = "G711u"; }
                            else if (outputarray[15] == "8") { outputarray[15] = "G711a"; }
                            else if (outputarray[15] == "9") { outputarray[15] = "G722"; }
                            else if (outputarray[15] == "18") { outputarray[15] = "G729"; }
                            else { outputarray[15] = "rtp-payload type:" + outputarray[15]; }
                            break;
                        case string s when (ua = uaRgx.Match(s)) != Match.Empty:
                            outputarray[16] = ua.ToString().Trim();
                            break;
                        case string s when (serv = serverRgx.Match(s)) != Match.Empty:
                            outputarray[16] = serv.ToString().Trim();
                            break;
                        case string s when occasRgx.IsMatch(s):
                            outputarray[16] = "occas";
                            break;
                    }
                    line = GetNextLine();
                    if (line == null) { break; }
                }
                
                // get the index of the end of the msg
                outputarray[9] = currentSplunkLoadProg.ToString();
                outputarray[10] = "Gray";
                outputarray[12] = "splunk"; //add file name 
                if (outputarray[5] == null) { outputarray[5] = "Invalid SIP characters"; }
                if (sipTwoDotOfound){
                    lock (_DataLocker) //messages touched by another thread 
                    {
                        messages.Add(outputarray);                        
                    }                        
                }
            }
        }
        else{
            currentSplunkLoadProg++;
        }
    }

    void AcReadData()
    {
        string line = GetNextLine();
        if (line != null)
        {
            while (!string.IsNullOrEmpty(line) && acBeginMsgRgx.IsMatch(line))
            {
                
                String[] outputarray = new String[18];

                // get the index of the start of the msg
                outputarray[0] = currentSplunkLoadProg.ToString(); 
                outputarray[1] = acBeginMsgRgx.Match(line).Groups["timedate"].ToString(); 
                outputarray[2] = DateTime.Parse(acBeginMsgRgx.Match(line).Groups["timedate"].ToString()).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture); 
                outputarray[3] = acBeginMsgRgx.Match(line).Groups["SrcIP"].ToString(); //src IP                                                                        
                outputarray[4] = acBeginMsgRgx.Match(line).Groups["DstIP"].ToString(); 
                outputarray[5] = acBeginMsgRgx.Match(line).Groups["req"].ToString(); 
                line = GetNextLine();               

                //check to match these only once. no need match a field if it is already found                
                Match callid;
                Match cseq;
                Match to;
                Match from; ;
                Match SDPIP;
                Match ua;
                Match serv;

                //untill the begining of the next msg
                while (!acBeginMsgRgx.IsMatch(line))
                { //match line against regexs
                    switch (line)
                    {
                        case string s when (callid = callidRgx.Match(s)) != Match.Empty:
                            outputarray[6] = callid.Groups[1].ToString(); 
                            break;
                        case string s when (cseq = cseqRgx.Match(s)) != Match.Empty:
                            outputarray[17] = cseq.Groups[2].ToString(); 
                            break;
                        case string s when (to = toRgx.Match(s)) != Match.Empty:
                            outputarray[7] = to.Groups[1].ToString() + to.Groups[3].ToString(); 
                            break;
                        case string s when (from = fromRgx.Match(s)) != Match.Empty:
                            outputarray[8] = from.Groups[1].ToString() + from.Groups[3].ToString(); 
                            break;
                        case string s when s.Contains("Content-Type: application/sdp"):
                            outputarray[11] = " SDP"; 
                            break;
                        case string s when (SDPIP = SDPIPRgx.Match(s)) != Match.Empty:
                            outputarray[13] = SDPIP.ToString(); 
                            break;
                        case string s when mAudioRgx.IsMatch(s):
                            outputarray[14] = portRgx.Match(s).ToString().Trim();
                            outputarray[15] = codecRgx.Match(s).ToString().Trim();
                            if (outputarray[15] == "0") { outputarray[15] = "G711u"; }
                            else if (outputarray[15] == "8") { outputarray[15] = "G711a"; }
                            else if (outputarray[15] == "9") { outputarray[15] = "G722"; }
                            else if (outputarray[15] == "18") { outputarray[15] = "G729"; }
                            else { outputarray[15] = "rtp-payload type:" + outputarray[15]; }
                            break;
                        case string s when (ua = uaRgx.Match(s)) != Match.Empty:
                            outputarray[16] = ua.ToString().Trim();
                            break;
                        case string s when (serv = serverRgx.Match(s)) != Match.Empty:
                            outputarray[16] = serv.ToString().Trim();
                            break;
                        case string s when occasRgx.IsMatch(s):
                            outputarray[16] = "occas";
                            break;
                    }
                    line = GetNextLine();
                    if (line == null) { break; }
                }

                // get the index of the end of the msg
                outputarray[9] = currentSplunkLoadProg.ToString();
                outputarray[10] = "Gray";
                outputarray[12] = "splunk"; //add file name 
                if (outputarray[5] == null) { outputarray[5] = "Invalid SIP characters"; }                
                lock (_DataLocker) //messages touched by another thread 
                {
                    messages.Add(outputarray);
                }                
            }
        }
        else
        {
            currentSplunkLoadProg++;
        }
    }

    void AcSyslogReadData()
    {
        string line = GetNextLine();
        if (line != null)
        {
            while (!string.IsNullOrEmpty(line) && acSyslogBeginMsgRgx.IsMatch(line))
            {

                String[] outputarray = new String[18];
                string milliSeconds;
                string yearStrg;

                // get the index of the start of the msg
                outputarray[0] = currentSplunkLoadProg.ToString();
                //outputarray[1] = acBeginMsgRgx.Match(line).Groups["timedate"].ToString();
                milliSeconds = acSyslogBeginMsgRgx.Match(line).Groups["ms"].ToString();
                
                outputarray[3] = acSyslogBeginMsgRgx.Match(line).Groups["SrcIP"].ToString(); //src IP                                                                        
                outputarray[4] = acSyslogBeginMsgRgx.Match(line).Groups["DstIP"].ToString();
                outputarray[5] = acSyslogBeginMsgRgx.Match(line).Groups["req"].ToString();
                line = GetNextLine();

                //check to match these only once. no need match a field if it is already found                
                Match callid;
                Match cseq;
                Match to;
                Match from; ;
                Match SDPIP;
                Match ua;
                Match serv;
                Match timeMatch;

                //untill the begining of the next msg
                while (!acSyslogBeginMsgRgx.IsMatch(line))
                { //match line against regexs
                    switch (line)
                    {
                        case string s when (callid = callidRgx.Match(s)) != Match.Empty:
                            outputarray[6] = callid.Groups[1].ToString();
                            break;
                        case string s when (cseq = cseqRgx.Match(s)) != Match.Empty:
                            outputarray[17] = cseq.Groups[2].ToString();
                            break;
                        case string s when (to = toRgx.Match(s)) != Match.Empty:
                            outputarray[7] = to.Groups[1].ToString() + to.Groups[3].ToString();
                            break;
                        case string s when (from = fromRgx.Match(s)) != Match.Empty:
                            outputarray[8] = from.Groups[1].ToString() + from.Groups[3].ToString();
                            break;
                        case string s when s.Contains("Content-Type: application/sdp"):
                            outputarray[11] = " SDP";
                            break;
                        case string s when (SDPIP = SDPIPRgx.Match(s)) != Match.Empty:
                            outputarray[13] = SDPIP.ToString();
                            break;
                        case string s when mAudioRgx.IsMatch(s):
                            outputarray[14] = portRgx.Match(s).ToString().Trim();
                            outputarray[15] = codecRgx.Match(s).ToString().Trim();
                            if (outputarray[15] == "0") { outputarray[15] = "G711u"; }
                            else if (outputarray[15] == "8") { outputarray[15] = "G711a"; }
                            else if (outputarray[15] == "9") { outputarray[15] = "G722"; }
                            else if (outputarray[15] == "18") { outputarray[15] = "G729"; }
                            else { outputarray[15] = "rtp-payload type:" + outputarray[15]; }
                            break;
                        case string s when (ua = uaRgx.Match(s)) != Match.Empty:
                            outputarray[16] = ua.ToString().Trim();
                            break;
                        case string s when (serv = serverRgx.Match(s)) != Match.Empty:
                            outputarray[16] = serv.ToString().Trim();
                            break;
                        case string s when occasRgx.IsMatch(s):
                            outputarray[16] = "occas";
                            break;
                        case string s when (timeMatch = acSyslogTimeRgx.Match(s)) != Match.Empty:
                            if (Int32.Parse(timeMatch.Groups["month"].ToString()) >= DateTime.Now.Month)
                            {
                                yearStrg = (DateTime.Now.Year - 1).ToString();
                            }
                            else
                            {
                                yearStrg = (DateTime.Now.Year).ToString();
                            }
                            outputarray[1] = yearStrg+"-"+timeMatch.Groups["month"].ToString()+"-"+ timeMatch.Groups["day"].ToString()+"T" + timeMatch.Groups["time"].ToString()+"."+ milliSeconds+"-05:00";
                            outputarray[2] = DateTime.Parse(outputarray[1]).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                            break;
                    }
                    line = GetNextLine();
                    if (line == null) { break; }
                }

                // get the index of the end of the msg
                outputarray[9] = currentSplunkLoadProg.ToString();
                outputarray[10] = "Gray";
                outputarray[12] = "splunk"; //add file name 
                if (outputarray[5] == null) { outputarray[5] = "Invalid SIP characters"; }
                lock (_DataLocker) //messages touched by another thread 
                {
                    messages.Add(outputarray);
                }
            }
        }
        else
        {
            currentSplunkLoadProg++;
        }
    }

    string GetNextLine()
    {
        string line;
        line = splunkSIPmessageSR.ReadLine();
       
        lock (_DataLocker) streamData.Add(line);  //touched by another threAD
        currentSplunkLoadProg++;
        return line;
    }

    void CallSelect()
    { //call display 
        bool done = false;
        CallListPosition = 0;
        CallDisplay(true);
        Console.SetCursorPosition(0, 4);
        Console.SetWindowPosition(0, 0);
        ConsoleKeyInfo keypressed;

        //loop until quit
        while (done == false){
            keypressed = Console.ReadKey(true);
            if (keypressed.Key == ConsoleKey.DownArrow){
                if (CallListPosition < callLegsDisplayed.Count - 1)
                {
                    MoveCursor(false, 1);
                }
            }
            if (keypressed.Key == ConsoleKey.PageDown){
                if (CallListPosition + 40 < callLegsDisplayed.Count - 1){
                    MoveCursor(false, 40);
                }
                else{
                    if (CallListPosition < callLegsDisplayed.Count - 1){
                        MoveCursor(false, (callLegsDisplayed.Count - 1) - CallListPosition);
                    }
                }
            }
            if (keypressed.Key == ConsoleKey.UpArrow) {
                if (CallListPosition > 0){
                    MoveCursor(true, 1);
                }
                else{
                    Console.SetWindowPosition(0, 0);
                }
            }
            if (keypressed.Key == ConsoleKey.PageUp){
                if (CallListPosition > 40) {
                    MoveCursor(true, 40);
                }
                else {
                    if (callLegsDisplayed.Count > 0) MoveCursor(true, CallListPosition);
                }
                if (CallListPosition == 0){
                    Console.SetWindowPosition(0, 0);
                }
            }
            if (callLegsDisplayed.Count > 0 && keypressed.Key == ConsoleKey.Spacebar) {

                if (callLegsDisplayed[CallListPosition][5] == "*") {

                    //find the index number of callLegs that matches callLegsDisplayed[CallListPosition] and change in callLegs so what call legs are selected persists if callLegsDisplayed is changed by the filter method. 
                    //cl string[] is the argument for the delegate that comes from List<string[]>callLegs.
                    //FindIndex crawls through callLegs indexes untill it receives true from the delegate, which tests if it matches callLegsDisplayed where the cursor postion is, and FindIndex retuns the index of callLegs.
                    int clindx = callLegs.FindIndex(
                        (cl) => {
                            if (cl == callLegsDisplayed[CallListPosition]) {
                                return true;
                            }
                            else {
                                return false;
                            }
                        }
                    );
                    callLegsDisplayed[CallListPosition][5] = " ";
                    callLegs[clindx][5] = " ";
                    numSelectedCalls--;
                    Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;   //change the colors of the current postion to inverted
                    Console.ForegroundColor = fieldConsoleTxtInvrtClr;
                    CallLine(callLegsDisplayed[CallListPosition], CallListPosition);
                    Console.CursorTop = Console.CursorTop - 1;
                }
                else{
                    
                    //find the index number of callLegs that matches callLegsDisplayed[CallListPosition] and change in callLegs so what call legs are selected persists if callLegsDisplayed is changed by the filter method. 
                    //cl string[] is the argument for the delegate that comes from List<string[]>callLegs.
                    //FindIndex crawls through callLegs indexes untill it receives true from the delegate, which tests if it matches callLegsDisplayed where the cursor postion is, and FindIndex retuns the index of callLegs.
                    int clindx = callLegs.FindIndex(
                        (cl)=>{
                            if (cl == callLegsDisplayed[CallListPosition]){
                                return true;
                            }
                            else{
                                return false;
                            }
                        }
                    );
                    callLegsDisplayed[CallListPosition][5] = "*";
                    lock (_DataLocker) callLegs[clindx][5] = "*";  // callLegs touched by other thread
                    numSelectedCalls++;
                    Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;   //change the colors of the current postion to inverted
                    Console.ForegroundColor = fieldConsoleTxtInvrtClr;
                    CallLine(callLegsDisplayed[CallListPosition], CallListPosition);
                    Console.CursorTop = Console.CursorTop - 1;
                }
                lock (_DataLocker) callIDsOfIntrest.Clear();
                SelectedCallsEarliestTime = DateTime.Now;
                SelectedCallsLatestTime = DateTime.Parse("2000-01-01T00:00:00.000-05:00");
                //find the selected calls from the call Legs Displayed
                lock (_DataLocker) for (int i = 0; i < callLegs.Count; i++){
                    if (callLegs[i][5] == "*"){
                        callIDsOfIntrest.Add(callLegs[i][4]);           //get the callIDs from the selected calls and add them to callIDsOfIntrest
                        if (DateTime.Parse(callLegs[i][0]) < SelectedCallsEarliestTime)
                        {
                            SelectedCallsEarliestTime = DateTime.Parse(callLegs[i][0]);
                        }
                        if (DateTime.Parse(callLegs[i][8]) > SelectedCallsLatestTime)
                        {
                              SelectedCallsLatestTime = DateTime.Parse(callLegs[i][0]).AddSeconds(1);
                        }
                    }
                }
            }
            if (numSelectedCalls > 0 && keypressed.Key == ConsoleKey.Enter){
                CancelSplunkJob = true;
                Console.SetWindowPosition(0, 0);
                lock (_DisplayLocker) displayMode = "flow";
                SplunkGetSIPMessages();                
                FlowSelect();   //select SIP message from the call flow diagram                        
                filterChange = true;
                CallFilter();
                lock (_DisplayLocker) displayMode = "calls";
                Console.SetWindowPosition(0, Math.Max(0, Console.CursorTop - Console.WindowHeight));
                CallDisplay(true);
            }
            if (keypressed.Key == ConsoleKey.Escape) {
                lock (_DisplayLocker){
                    Console.ForegroundColor = msgBoxTxt;
                    Console.BackgroundColor = msgBoxBkgrd;
                    int center = Math.Max(0, (int)Math.Floor((decimal)((Console.WindowWidth - 42) / 2)));
                    Console.CursorLeft = center; Console.WriteLine(@"+--------------------------------------+\ ");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Are you sure you want to quit? Y/N? | |");
                    Console.CursorLeft = center; Console.WriteLine(@"+--------------------------------------+ |");
                    Console.CursorLeft = center; Console.WriteLine(@" \______________________________________\|");
                    Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
                    Console.ForegroundColor = fieldConsoleTxtClr;
                    bool tryAgain = true;
                    while (tryAgain)
                    switch (Console.ReadKey(true).Key){
                        case ConsoleKey.Y:
                            Console.Clear();
                            System.Environment.Exit(0);
                            break;
                        case ConsoleKey.N:
                            tryAgain = false;
                            filterChange = true;
                            CallFilter();
                            Console.SetWindowPosition(0, Math.Max(0, Console.CursorTop - Console.WindowHeight));
                            CallDisplay(true);
                            break;
                    }
                }
            }
            if (keypressed.Key == ConsoleKey.H) {
                lock (_DisplayLocker){
                    Console.ForegroundColor = msgBoxTxt;
                    Console.BackgroundColor = msgBoxBkgrd;
                    int center = Math.Max(0, (int)Math.Floor((decimal)((Console.WindowWidth - 71) / 2)));
                    Console.CursorLeft = center; Console.WriteLine(@"+-------------------------------------------------------------------+\ ");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Key                                                              | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Down Arrow / Page Down ------------------------ move cursor down | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Up Arrow / Page Up ------------------------------ move cursor up | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Left Arrow / Right Arrow --------------------- change sort order | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Spacebar ------------------------------------ select call leg(s) | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Enter -------------------------------- Show diagram of call flow | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Esc --------------------------------------- Exit the application | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  H --------------------------------------------- This help dialog | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  M -------------------------------------- Search all SIP messages | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Q ------------------------------------------- Query splunk again | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  C ------------------------------------- Write configuration file | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  F ----------------------------------- Filter the displayed calls | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  N ------------------------------------ Toggle display of NOTIFYs | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  O ------------------- Search Option Messages and 200 OK response | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  R ------------------------------------------- Show registrations | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  S ------------------------------------------ Show subscritptions | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  I -------------------------------------- Show calls with INTIVES | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  T -- Toggle time zone displyed: local, UTC, timestamp of the log | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|                                                                   | |");
                    Console.CursorLeft = center; Console.WriteLine(@"+-------------------------------------------------------------------+ |");
                    Console.CursorLeft = center; Console.WriteLine(@" \___________________________________________________________________\|");
                    Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
                    Console.ForegroundColor = fieldConsoleTxtClr;
                    Console.ReadKey(true);
                    filterChange = true;
                    CallFilter();
                    Console.SetWindowPosition(0, Math.Max(0, Console.CursorTop - Console.WindowHeight));
                    CallDisplay(true);
                }
            }
            if (keypressed.Key == ConsoleKey.M){
                lock (_DisplayLocker) { 
                    do{
                        displayMode = "messages";
                        ListAllMsg(null);
                        Console.ForegroundColor = msgBoxTxt;
                        Console.BackgroundColor = msgBoxBkgrd;
                        int center = Math.Max(0, (int)Math.Floor((decimal)((Console.WindowWidth - 71) / 2)));
                        Console.CursorLeft = center; Console.WriteLine(@"+-------------------------------------------------------------------+\ ");
                        Console.CursorLeft = center; Console.WriteLine(@"|  Press any key to query SIP messages again or press [esc] to quit | |");
                        Console.CursorLeft = center; Console.WriteLine(@"+-------------------------------------------------------------------+ |");
                        Console.CursorLeft = center; Console.WriteLine(@" \___________________________________________________________________\|");
                        Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
                        Console.ForegroundColor = fieldConsoleTxtClr;
                    } while (Console.ReadKey(true).Key != ConsoleKey.Escape);
                    filterChange = true;
                    CallFilter();
                    Console.SetWindowPosition(0, Math.Max(0, Console.CursorTop - Console.WindowHeight));
                    CallDisplay(true);
                    displayMode = "calls";
                }
            }
            if (keypressed.Key == ConsoleKey.Q){
                CancelSplunkJob = true;
                displayMode = "splunkqueryentry";
                ClearConsoleNoTop();

                //prompt for new splunk prameters 
                Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
                Console.ForegroundColor = fieldConsoleTxtClr;
                Console.SetWindowPosition(0, 0);
                Console.SetCursorPosition(0, 1);
                Regex earliestTimeAndDateRGX = new Regex(@"\d{4}-\d{2}-\d{1,2}T\d{2}:\d{2}:\d{2}.\d{3}(-|\+)\d{2}:\d{2}|-\d{1,3}\s*(s|m|h|d|w|m|q|y)", RegexOptions.IgnoreCase);
                Regex latestTimeAndDateRGX = new Regex(@"\d{4}-\d{2}-\d{1,2}T\d{2}:\d{2}:\d{2}.\d{3}(-|\+)\d{2}:\d{2}|-\d{1,3}\s*(s|m|h|d|w|m|q|y)|now", RegexOptions.IgnoreCase);
                bool goodentry = false;
                while (!goodentry) {
                    Console.Write("Enter Splunk API URL [{0}]: ", splunkUrl);
                    string splunkUrlEntry = Console.ReadLine();
                    if (!string.IsNullOrEmpty(splunkUrlEntry)) { splunkUrl = splunkUrlEntry; }
                    if (splunkUrl.StartsWith("https://") && Uri.IsWellFormedUriString(splunkUrl, UriKind.RelativeOrAbsolute)) { goodentry = true; }
                }
                goodentry = false;
                while (!goodentry){
                    Console.Write("Enter Splunk user  [{0}]: ", user);
                    string userEntry = Console.ReadLine();
                    if (!string.IsNullOrEmpty(userEntry)) { user = userEntry; }
                    if (user != null) { goodentry = true; }
                }
                goodentry = false;
                while (!goodentry){
                    Console.Write("Enter Splunk user " + user + " password : ");
                    ConsoleKeyInfo key;
                    bool clearedPassword = false;
                    do
                    {
                        key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Backspace)
                        {
                            if (password.Length > 0)
                            {
                                password.RemoveAt(password.Length - 1);
                                Console.Write("\b \b");
                            }
                        }
                        if (((decimal)key.KeyChar) >= 32 && ((decimal)key.KeyChar <= 126))
                        {
                            if (!clearedPassword)
                            {
                                clearedPassword = true;
                                password.Clear();
                            }
                            password.AppendChar(key.KeyChar);
                            Console.Write("*");
                        }
                    } while (key.Key != ConsoleKey.Enter);                 
                    
                    Console.WriteLine();
                    if (password.Length>0) goodentry = true;
                }
                goodentry = false;
                while (!goodentry){
                    Console.Write("Enter Splunk search string. Must contain \"index=\"  [{0}]: ", searchStrg);
                    string searchStrgEntry = Console.ReadLine();
                    if (!string.IsNullOrEmpty(searchStrgEntry)) { searchStrg = searchStrgEntry; }
                    if (!string.IsNullOrEmpty(searchStrg) || searchStrg.Contains("index=")) { goodentry = true; }
                }
                goodentry = false;                
                while (!goodentry)
                {
                    Console.Write("Enter the log type tcpdump, audiocodes or audiocodesSyslog [{0}]: ", logMode);
                    string logModeEntry = Console.ReadLine();
                    if (!string.IsNullOrEmpty(logModeEntry)) { logMode = logModeEntry; }
                    if (logMode.Contains("tcpdump") || logMode.Contains("audiocodes") || logMode.Contains("audiocodesSyslog")) { goodentry = true; }
                }                
                goodentry = false;
                bool goodTimeEntry = false;
                while (!goodTimeEntry){
                    while (!goodentry){
                        Console.Write("Enter search begining time [{0}]: ", earliest);
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("YYYY-MM-DDTHH:mm:ss.sss+hh:mm");
                        Console.CursorLeft -= 29;
                        Console.ForegroundColor = ConsoleColor.Gray;
                        string earliestEntry = Console.ReadLine();
                        if (!string.IsNullOrEmpty(earliestEntry)) { earliest = earliestEntry; }
                        if (earliestTimeAndDateRGX.IsMatch(earliest)) { goodentry = true; }
                    }
                    goodentry = false;
                    while (!goodentry) {
                        Console.Write("Enter search end time [{0}]: ", latest);
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("YYYY-MM-DDTHH:mm:ss.sss+hh:mm");
                        Console.CursorLeft -= 29;
                        Console.ForegroundColor = ConsoleColor.Gray;
                        string latestEntry = Console.ReadLine();
                        if (!string.IsNullOrEmpty(latestEntry)) { latest = latestEntry; }
                        if (latestTimeAndDateRGX.IsMatch(latest)) { goodentry = true; }
                    }
                    if (Regex.IsMatch(latest, @"\d{4}-\d{2}-\d{1,2}T\d{2}:\d{2}:\d{2}.\d{3}(-|\+)\d{2}:\d{2}") && Regex.IsMatch(earliest, @"\d{4}-\d{2}-\d{1,2}T\d{2}:\d{2}:\d{2}.\d{3}(-|\+)\d{2}:\d{2}")) {
                        if (DateTime.Parse(earliest) > DateTime.Parse(latest)) {
                            Console.Write("start time is later than end time");
                            earliest = "";
                            latest = "";
                        }
                        else{
                            goodTimeEntry = true;
                        }
                    }
                    else{
                        goodTimeEntry = true;
                    }
                }
                Array.Clear(filter, 0, filter.Length - 1);
                streamData.Clear();
                currentSplunkLoadProg = 0;
                messages.Clear();
                callLegs.Clear();
                callLegsDisplayed.Clear();
                selectedmessages.Clear();
                IPsOfIntrest.Clear();
                callIDsOfIntrest.Clear();
                CallInvites = 0;
                notifications = 0;
                registrations = 0;
                subscriptions = 0;
                callLegsDisplayedCountPrev = 0;
                prevNumSelectdIPs = 0;
                prevNumSelectMsg = 0;
                IPprevNumSelectMsg = 0;
                numSelectedCalls = 0;
                lock (_QueryAgainlocker){
                    Monitor.Pulse(_QueryAgainlocker);
                }
                displayMode = "calls";
                CallListPosition = 0;
                CallDisplay(true);
                Console.SetCursorPosition(0, 4);
                Console.SetWindowPosition(0, 0);
            }
            if (SplunkReadDone && keypressed.Key == ConsoleKey.C){
                bool goodentry = false;
                while (!goodentry){
                    Console.ForegroundColor = msgBoxTxt;
                    Console.BackgroundColor = msgBoxBkgrd;
                    int center = Math.Max(0, (int)Math.Floor((decimal)((Console.WindowWidth - 136) / 2)));
                    Console.CursorLeft = center; Console.WriteLine(@"+---------------------------------------------------------------------------------------------------------+\ ");
                    Console.CursorLeft = center; Console.WriteLine(@"| Enter the file name of the config file to be created. .sls will be appended to the end of the file name | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|                                                                                                         | |");
                    Console.CursorLeft = center; Console.WriteLine(@"+---------------------------------------------------------------------------------------------------------+ |");
                    Console.CursorLeft = center; Console.WriteLine(@" \_________________________________________________________________________________________________________\|");
                    Console.CursorTop -= 3;
                    Console.CursorLeft = center + 2;
                    string writeFileName = Console.ReadLine();
                    if (String.IsNullOrEmpty(writeFileName)){
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.CursorTop = Console.CursorTop - 1;
                        Console.CursorLeft = center;
                        Console.WriteLine("| No file name was entered. Press any key to continue");
                        Console.ForegroundColor = fieldConsoleTxtClr;
                        Console.CursorVisible = true;
                        Console.ReadKey(true);
                        Console.CursorTop -= 3;
                        goodentry = true;
                    }
                    else {
                        writeFileName = writeFileName + ".sls";
                        try{
                            StreamWriter flowConfigFileWriter = new StreamWriter(writeFileName);
                            flowConfigFileWriter.WriteLine(splunkUrl);
                            flowConfigFileWriter.WriteLine(searchStrg);
                            flowConfigFileWriter.WriteLine(earliest);
                            flowConfigFileWriter.WriteLine(latest);
                            flowConfigFileWriter.WriteLine(logMode);
                            flowConfigFileWriter.Close();
                        }
                        catch (IOException e){
                            TextWriter errorWriter = Console.Error;
                            errorWriter.WriteLine(e.Message);
                        }
                        goodentry = true;
                        
                    }
                    Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
                    Console.ForegroundColor = fieldConsoleTxtClr;
                    CallDisplay(true);
                    Console.SetWindowPosition(0, Math.Min (Math.Max(0, Console.CursorTop - Console.WindowHeight/2), Console.BufferHeight- Console.WindowHeight));
                }
            }
            if (keypressed.Key == ConsoleKey.F){
                lock (_DisplayLocker){
                    filterChange = true;
                    Console.ForegroundColor = msgBoxTxt;
                    Console.BackgroundColor = msgBoxBkgrd;
                    int center = Math.Max(0, (int)Math.Floor((decimal)((Console.WindowWidth - 136) / 2)));
                    Console.CursorLeft = center; Console.WriteLine(@"+------------------------------------------------------------------------------------------------------------------------------------+\ ");
                    Console.CursorLeft = center; Console.WriteLine(@"| Enter space separated items like extensions, names or IP. Items are OR. Case sensitive. Leave blank for no Filter.                 | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|                                                                                                                                    | |");
                    Console.CursorLeft = center; Console.WriteLine(@"+------------------------------------------------------------------------------------------------------------------------------------+ |");
                    Console.CursorLeft = center; Console.WriteLine(@" \____________________________________________________________________________________________________________________________________\|");
                    Console.CursorTop -= 3;
                    Console.CursorLeft = center + 2;
                    filter = Console.ReadLine().Split(' ');
                    CallFilter();
                    if (SplunkReadDone) { SortCalls(); }
                    Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
                    Console.ForegroundColor = fieldConsoleTxtClr;
                    CallListPosition = 0;
                    CallDisplay(true);
                    Console.SetWindowPosition(0, 0);
                    Console.SetCursorPosition(0, 4);
                }
            }
            if (keypressed.Key == ConsoleKey.N){
                CallListPosition = 0;
                lock (_DisplayLocker) if (showNotify == false) { showNotify = true; } else { showNotify = false; }
                filterChange = true;
                CallFilter();
                if (SplunkReadDone) { SortCalls(); }
                Console.SetWindowPosition(0, Math.Max(0, Console.CursorTop - Console.WindowHeight));
                CallDisplay(true);
            }
            if (keypressed.Key == ConsoleKey.T){
                if (timeMode == TZmode.stamp){
                    timeMode = TZmode.local;
                }
                else {
                    timeMode++;
                }                
                CallDisplay(true);
            }
            if (keypressed.Key == ConsoleKey.O){
                lock (_DisplayLocker) {
                    Console.ForegroundColor = msgBoxTxt;
                    Console.BackgroundColor = msgBoxBkgrd;
                    int center = Math.Max(0, (int)Math.Floor((decimal)((Console.WindowWidth - 136) / 2)));
                    Console.CursorLeft = center; Console.WriteLine(@"+-------------------------------------------------------------------------+\ ");
                    Console.CursorLeft = center; Console.WriteLine(@"| Enter the IP address of the device to view if it is answering OPTIONS : | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|                                                                         | |");
                    Console.CursorLeft = center; Console.WriteLine(@"+-------------------------------------------------------------------------+ |");
                    Console.CursorLeft = center; Console.WriteLine(@" \_________________________________________________________________________\|");
                    Console.CursorTop -= 3;
                    Console.CursorLeft = center + 2;
                    string IPaddr = Console.ReadLine();
                    if (String.IsNullOrEmpty(IPaddr)){
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.CursorTop = Console.CursorTop - 1;
                        Console.CursorLeft = center;
                        Console.WriteLine("| No IP address was entered. Press any key to continue");
                        Console.ForegroundColor = fieldConsoleTxtClr;
                        Console.CursorVisible = true;
                        Console.ReadKey(true);
                        Console.CursorTop -= 4;
                    }
                    else{
                        displayMode = "messages";
                        ListAllMsg(IPaddr + ".*OPTIONS|" + IPaddr + ".*200 OK.*OPTIONS");
                    }
                    Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
                    Console.ForegroundColor = fieldConsoleTxtClr;
                    filterChange = true;
                    CallFilter();
                    if (SplunkReadDone) { SortCalls(); }
                    Console.SetWindowPosition(0, Math.Max(0, Console.CursorTop - Console.WindowHeight));
                    CallDisplay(true);
                }
            }
            if (methodDisplayed != "REGISTER" && keypressed.Key == ConsoleKey.R){
                string prevMethod = methodDisplayed;
                filterChange = true;
                lock (_DisplayLocker) methodDisplayed = "REGISTER";
                ClearSelectedCalls();
                numSelectedCalls = 0;
                CallListPosition = 0;
                CallFilter();
                if (SplunkReadDone) { SortCalls(); }
                CallDisplay(true);
                Console.SetWindowPosition(0, 0);
                Console.SetCursorPosition(0, 4);
            }
            if (methodDisplayed != "SUBSCRIBE" && keypressed.Key == ConsoleKey.S){
                string prevMethod = methodDisplayed;
                filterChange = true;
                ClearSelectedCalls();
                numSelectedCalls = 0;
                lock (_DisplayLocker) methodDisplayed = "SUBSCRIBE";
                CallListPosition = 0;
                CallFilter();
                if (SplunkReadDone) { SortCalls(); }
                CallDisplay(true);
                Console.SetWindowPosition(0, 0);
                Console.SetCursorPosition(0, 4);
            }
            if (methodDisplayed != "INVITE" && keypressed.Key == ConsoleKey.I){
                string prevMethod = methodDisplayed;
                filterChange = true;
                lock (_DisplayLocker) methodDisplayed = "INVITE";
                numSelectedCalls = 0;
                ClearSelectedCalls();
                CallListPosition = 0;
                CallFilter();
                if (SplunkReadDone) { SortCalls(); }
                CallDisplay(true);
                Console.SetWindowPosition(0, 0);
                Console.SetCursorPosition(0, 4);
            }
            if (SplunkReadDone && keypressed.Key == ConsoleKey.LeftArrow){
                if (callsDisplaysortIdx > 0){
                    WriteScreen(sortFields[callsDisplaysortIdx, 0], Int16.Parse(sortFields[callsDisplaysortIdx, 1]), 2, headerTxtClr, headerBkgrdClr);
                    callsDisplaysortIdx--;
                    CallFilter();
                    SortCalls();
                    CallDisplay(true);
                    Console.SetWindowPosition(0, 0);
                    Console.SetCursorPosition(0, 4);
                }
            }
            if (SplunkReadDone && keypressed.Key == ConsoleKey.RightArrow){
                if (callsDisplaysortIdx < 4){
                    WriteScreen(sortFields[callsDisplaysortIdx, 0], Int16.Parse(sortFields[callsDisplaysortIdx, 1]), 2, headerTxtClr, headerBkgrdClr);
                    callsDisplaysortIdx++;
                    CallFilter();
                    SortCalls();
                    CallDisplay(true);
                    Console.SetWindowPosition(0, 0);
                    Console.SetCursorPosition(0, 4);
                }
            }
        }
    }

    void MoveCursor(bool up, int amount){
        lock (_DisplayLocker) {
            Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
            Console.ForegroundColor = fieldConsoleTxtClr;
            CallLine(callLegsDisplayed[CallListPosition], CallListPosition);
            if (up) {
                CallListPosition -= amount;
                Console.CursorTop -= (amount + 1);
            }
            else {
                CallListPosition += amount;
                Console.CursorTop += (amount - 1);
            }
            Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;   //change the colors of the current postion to inverted
            Console.ForegroundColor = fieldConsoleTxtInvrtClr;
            CallLine(callLegsDisplayed[CallListPosition], CallListPosition);
            Console.CursorTop -= 1;
            Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
            Console.ForegroundColor = fieldConsoleTxtClr;
        }
    }

    void SortCalls(){
        if (callsDisplaysortIdx == 0){
            callLegsDisplayed = callLegsDisplayed.OrderBy(UTC => UTC[1]).ToList();
            CallListPosition = 0;
        }
        else{
            callLegsDisplayed = callLegsDisplayed.OrderBy(field => field[Int16.Parse(sortFields[callsDisplaysortIdx, 2])]).ToList();
            CallListPosition = 0;
        }
    }

    void ClearSelectedCalls(){
        for (int i = 0; i < callLegsDisplayed.Count; i++){
            callLegsDisplayed[i][5] = " ";
        }
    }

    void CallDisplay(bool newFullScreen){
        lock (_DisplayLocker){
            Console.WindowWidth = Math.Min(161, Console.LargestWindowWidth);
            Console.WindowHeight = Math.Min(44, Console.LargestWindowHeight);
            Console.BufferWidth = 200;
            Console.BufferHeight = Math.Max(10 + callLegsDisplayed.Count, Console.BufferHeight);
            //if the following conditions true , just add the calls to the bottom of the screen without redrawing
            if (!newFullScreen && !filterChange && callLegsDisplayedCountPrev != 0 && callLegsDisplayed.Count > callLegsDisplayedCountPrev){
                for (int i = callLegsDisplayedCountPrev; i < callLegsDisplayed.Count; i++){
                    WriteScreenCallLine(callLegsDisplayed[i], i);
                }
            }
            else{
                string timeString = "";
                switch (timeMode) {
                    case TZmode.local: {
                            timeString = "Local Time";
                            break;
                        }
                    case TZmode.utc:{
                            timeString = "UTC Time";
                            break;
                        }
                    case TZmode.stamp:{
                            timeString = "Time Stamp";
                            break;
                        }
                }
                sortFields[0, 0] = timeString;
                filterChange = false;
                callLegsDisplayedCountPrev = callLegsDisplayed.Count;
                ClearConsoleNoTop();
                fakeCursor[0] = 0; fakeCursor[1] = 1;
                WriteConsole("[Spacebar]-select calls [Enter]-for call flow [Q]-query splunk again [F]-filter [H]-help [Esc]-quit", headerTxtClr, headerBkgrdClr);
                WriteLineConsole(" ", headerTxtClr, headerBkgrdClr);
                String formatedStr = String.Format("{0,-2} {1,-6} {2,-10} {3,-12} {4,-30} {5,-30} {6,-16} {7,-16}", "*", "Index", "Date", timeString, "From:", "To:", "Src IP", "Dst IP");
                WriteLineConsole(formatedStr, headerTxtClr, headerBkgrdClr);
                if (methodDisplayed == "INVITE") { WriteConsole("----invites/calls---", headerTxtClr, headerBkgrdClr); }
                if (methodDisplayed == "REGISTER") { WriteConsole("----registrations---", headerTxtClr, headerBkgrdClr); }
                if (methodDisplayed == "SUBSCRIBE") { WriteConsole("----subscriptions---", headerTxtClr, headerBkgrdClr); }
                WriteLineConsole(new String('-', 140), headerTxtClr, headerBkgrdClr);
                if (callLegsDisplayed.Count > 0) {
                    for (int i = 0; i < callLegsDisplayed.Count; i++){
                        WriteScreenCallLine(callLegsDisplayed[i], i);
                    }
                }
                WriteScreen(sortFields[callsDisplaysortIdx, 0], Int16.Parse(sortFields[callsDisplaysortIdx, 1]), 2, sortTxtdClr, sortBkgrdClr);
            }
            string footerTwo = "Number of SIP transactions found : " + callLegs.Count.ToString();
            string footerThree = CallInvites.ToString() + " SIP INVITEs found | " + notifications.ToString() + " SIP NOTIFYs found | " + registrations.ToString() + " SIP REGISTERs found | " + subscriptions.ToString() + " SIP SUBSCRIBEs found";            
            WriteScreen(footerTwo + new String(' ', Console.BufferWidth - footerTwo.Length), 0, (short)(callLegsDisplayed.Count + 4), footerTxtClr, footerBkgrdClr);
            WriteScreen(footerThree + new String(' ', Console.BufferWidth - footerThree.Length), 0, (short)(callLegsDisplayed.Count + 5), footerTxtClr, footerBkgrdClr);            
            if (callLegsDisplayed.Count > 0) {
                Console.SetCursorPosition(0, CallListPosition + 4);
                Console.BackgroundColor = fieldConsoleTxtClr;
                Console.ForegroundColor = fieldConsoleBkgrdClr;
                CallLine(callLegsDisplayed[CallListPosition], CallListPosition);
                Console.SetCursorPosition(0, CallListPosition + 4);
                Console.BackgroundColor = fieldConsoleBkgrdClr;
                Console.ForegroundColor = fieldConsoleTxtClr;
            }
        }
    }

    void CallLine(string[] InputCallLegs, int indx) {
        if (InputCallLegs.Length == 10) {
            if (InputCallLegs[5] == "*"){
                Console.ForegroundColor = fieldConsoleSelectClr;
            }
            string dateString = "";
            string timeString = "";
            switch (timeMode){
                case TZmode.local: {
                        dateString = DateTime.Parse(InputCallLegs[1]).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        timeString = DateTime.Parse(InputCallLegs[1]).ToLocalTime().ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture);
                        break;
                    }
                case TZmode.utc:{
                        dateString = DateTime.Parse(InputCallLegs[1]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        timeString = DateTime.Parse(InputCallLegs[1]).ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture);
                        break;
                    }
                case TZmode.stamp:{
                        dateString = DateTime.Parse(InputCallLegs[0]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        timeString = DateTime.Parse(InputCallLegs[0]).ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture);
                        break;
                    }
            }
            Console.WriteLine("{0,-2} {1,-6} {2,-10} {3,-12} {5,-30} {4,-30} {6,-16} {7,-17}"
            , InputCallLegs[5]
            , indx
            , dateString
            , timeString
            , InputCallLegs[2]
            , InputCallLegs[3]
            , InputCallLegs[6]
            , InputCallLegs[7]);
            Console.ForegroundColor = fieldConsoleTxtClr;
        }
    }

    void WriteScreenCallLine(string[] callLeg, int indx){
        if (callLeg.Length == 10){
            AttrColor txtColor;
            AttrColor bkgrdColor;
            short y = (short)(indx + 4);
            if (callLeg[5] == "*"){
                txtColor = fieldAttrSelectClr;
                if (indx == CallListPosition){
                    bkgrdColor = fieldAttrBkgrdInvrtClr;
                }
                else{
                    bkgrdColor = fieldAttrBkgrdClr;
                }
            }
            else{
                txtColor = fieldAttrTxtClr;
                if (indx == CallListPosition){
                    txtColor = fieldAttrTxtInvrtClr;
                    bkgrdColor = fieldAttrBkgrdInvrtClr;
                }
                else {
                    txtColor = fieldAttrTxtClr;
                    bkgrdColor = fieldAttrBkgrdClr;
                }
            }
            string dateString = "";
            string timeString = "";
            switch (timeMode){
                case TZmode.local:{
                        dateString = DateTime.Parse(callLeg[1]).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        timeString = DateTime.Parse(callLeg[1]).ToLocalTime().ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture);
                        break;
                    }
                case TZmode.utc:{
                        dateString = DateTime.Parse(callLeg[1]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        timeString = DateTime.Parse(callLeg[1]).ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture);
                        break;
                    }
                case TZmode.stamp:{
                        dateString = DateTime.Parse(callLeg[0]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        timeString = DateTime.Parse(callLeg[0]).ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture);
                        break;
                    }
            }
            string formatedStr = String.Format("{0,-2} {1,-6} {2,-10} {3,-12} {5,-30} {4,-30} {6,-16} {7,-17}"
                , callLeg[5]
                , indx
                , dateString
                , timeString
                , callLeg[2]
                , callLeg[3]
                , callLeg[6]
                , callLeg[7]);
            WriteScreen(formatedStr, 0, y, txtColor, bkgrdColor);
        }
    }

    void CallFilter() {
        lock (_DataLocker){
            callLegsDisplayed.Clear();
            if (!string.IsNullOrEmpty(filter[0])) {
                for (int i = 0; i < callLegs.Count; i++){
                    bool addcall = false;
                    for (int j = 0; j < callLegs[i].Length; j++){
                        String callitem = callLegs[i][j];
                        if (!String.IsNullOrEmpty(callitem)) {
                            foreach (String filteritem in filter) {
                                if (callitem.Contains(filteritem)){
                                    if ((showNotify && callLegs[i][9] == "NOTIFY") || (callLegs[i][9] == methodDisplayed)) { addcall = true; }
                                }
                            }
                        }
                    }
                    if (addcall) { callLegsDisplayed.Add(callLegs[i]); }
                }
            }
            else {
                for (int i = 0; i < callLegs.Count; i++) {
                    bool addcall = false;
                    foreach (String callitem in callLegs[i]){
                        foreach (String filteritem in filter){
                            if ((showNotify && callLegs[i][9] == "NOTIFY") || (callLegs[i][9] == methodDisplayed)) { addcall = true; }
                        }
                    }
                    if (addcall) { callLegsDisplayed.Add(callLegs[i]); }
                }
            }
        }
    }

    void FlowSelect()
    {
        prevNumSelectMsg = selectedmessages.Count;
        flowSelectPosition = 0;
        Flow(false);  //display call flow Diagram
        if (selectedmessages.Count == 0)
        {
            return;
        }
        bool done = false;
        while (done == false)
        {
            ConsoleKeyInfo keypress;
            while (Console.KeyAvailable) { Console.ReadKey(true); } //clear any already pressed keys that could have been pressed during wait
            keypress = Console.ReadKey(true);
            if (keypress.Key == ConsoleKey.DownArrow)
            {
                if (flowSelectPosition < selectedmessages.Count - 1)
                {
                    MessageLine(selectedmessages[flowSelectPosition], false);
                    flowSelectPosition++;
                    MessageLine(selectedmessages[flowSelectPosition], true);
                    Console.CursorTop -= 1;
                }
            }
            else if (keypress.Key == ConsoleKey.PageDown)
            {
                if (flowSelectPosition + 40 < selectedmessages.Count - 1)
                {
                    MessageLine(selectedmessages[flowSelectPosition], false);
                    flowSelectPosition += 40;
                    Console.CursorTop += 39;
                    MessageLine(selectedmessages[flowSelectPosition], true);
                    Console.CursorTop -= 1;
                }
                else
                {
                    MessageLine(selectedmessages[flowSelectPosition], false);
                    flowSelectPosition = selectedmessages.Count - 1;
                    Console.CursorTop = selectedmessages.Count - 1 + 4;
                    MessageLine(selectedmessages[flowSelectPosition], true);
                    Console.CursorTop -= 1;
                }
            }
            else if (keypress.Key == ConsoleKey.UpArrow)
            {
                if (flowSelectPosition > 0)
                {
                    MessageLine(selectedmessages[flowSelectPosition], false);
                    Console.CursorTop -= 2;
                    flowSelectPosition--;
                    MessageLine(selectedmessages[flowSelectPosition], true);
                    Console.CursorTop -= 1;
                }
                else
                {
                    Console.SetCursorPosition(0, 0);   //brings window to the very top
                    Console.SetCursorPosition(0, 4);
                }
            }
            else if (keypress.Key == ConsoleKey.PageUp)
            {
                if (flowSelectPosition > 39)
                {
                    MessageLine(selectedmessages[flowSelectPosition], false);
                    Console.CursorTop -= 41;
                    flowSelectPosition -= 40;
                    MessageLine(selectedmessages[flowSelectPosition], true);
                    Console.CursorTop -= 1;
                }
                else
                {
                    MessageLine(selectedmessages[flowSelectPosition], false);
                    Console.CursorTop = 4;
                    flowSelectPosition = 0;
                    MessageLine(selectedmessages[flowSelectPosition], true);
                    Console.CursorTop -= 1;
                }
                if (flowSelectPosition == 0)
                {
                    Console.SetCursorPosition(0, 0);   //brings window to the very top
                    Console.SetCursorPosition(0, 4);
                }
            }
            else if ((keypress.Key == ConsoleKey.Enter) || (keypress.Key == ConsoleKey.Spacebar))
            {
                DisplayMessage(flowSelectPosition, selectedmessages);
                Flow(false);  //display call flow Diagram
            }
            else if (keypress.Key == ConsoleKey.Escape)
            {
                done = true;
            }
            else if (keypress.Key == ConsoleKey.D)
            {
                if (dupIP)
                {
                    dupIP = false;
                }
                else
                {
                    dupIP = true;
                }
                flowSelectPosition = 0;
                Flow(false);  //display call flow Diagram
            }
            else if (keypress.Key == ConsoleKey.O)
            {
                Console.ForegroundColor = msgBoxTxt;
                Console.BackgroundColor = msgBoxBkgrd;
                int center = Math.Max(0, (int)Math.Floor((decimal)((Console.WindowWidth - 136) / 2)));
                Console.CursorLeft = center; Console.WriteLine(@"+---------------------------------------------------------------------------+\ ");
                Console.CursorLeft = center; Console.WriteLine(@"| Enter the file name to the data will be writen to (.html for html output) | |");
                Console.CursorLeft = center; Console.WriteLine(@"|                                                                           | |");
                Console.CursorLeft = center; Console.WriteLine(@"+---------------------------------------------------------------------------+ |");
                Console.CursorLeft = center; Console.WriteLine(@" \___________________________________________________________________________\|");
                Console.CursorTop -= 3;
                Console.CursorLeft = center + 2;
                string writeFileName = Console.ReadLine();
                if (String.IsNullOrEmpty(writeFileName))
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.CursorTop = Console.CursorTop - 1;
                    Console.CursorLeft = center;
                    Console.WriteLine("| No file name was entered. Press any key to continue");
                    Console.ForegroundColor = fieldConsoleTxtClr;
                    Console.CursorVisible = true;
                    Console.ReadKey(true);
                    Console.CursorTop -= 4;
                }
                else
                {
                    if (Regex.IsMatch(writeFileName, @"^.*\.html$")) { htmlFlowToFile = true; }
                    try
                    {
                        flowFileWriter = new StreamWriter(writeFileName);
                    }
                    catch (IOException e)
                    {
                        TextWriter errorWriter = Console.Error;
                        errorWriter.WriteLine(e.Message);
                    }
                    writeFlowToFile = true;
                    if (htmlFlowToFile)
                    {
                        flowFileWriter.WriteLine("<!DOCTYPE html>");
                        flowFileWriter.WriteLine("<html>");
                        flowFileWriter.WriteLine("<head>");
                        flowFileWriter.WriteLine("<style> a:link {text-decoration: none} ");
                        flowFileWriter.WriteLine(".hosts {");
                        flowFileWriter.WriteLine("  position: fixed;");
                        flowFileWriter.WriteLine("  background-color: white;");
                        flowFileWriter.WriteLine("  top: 0;");
                        flowFileWriter.WriteLine("}");
                        flowFileWriter.WriteLine(".main {");
                        flowFileWriter.WriteLine("  margin-top: 4.5em;");
                        flowFileWriter.WriteLine("}");
                        flowFileWriter.WriteLine("</style>");
                        flowFileWriter.WriteLine("<font face=\"Courier\" >");
                        flowFileWriter.WriteLine("</head>");
                        flowFileWriter.WriteLine("<body>");
                        flowFileWriter.WriteLine("<pre>");
                    }                    
                    Flow(false);  //display call flow Diagram
                    flowFileWriter.WriteLine("Created with SIPlogSplunk https://github.com/gregp203/Splunk-SIPlog ");
                    flowFileWriter.WriteLine(" ");
                    flowFileWriter.WriteLine(" ");
                    Console.SetCursorPosition(0, 1);
                    Console.SetWindowPosition(0, 0);
                    //cycle through all the seleted msg 
                    for (int i = 0; i < selectedmessages.Count; i++)
                    {
                        string line = "";
                        Console.WriteLine();
                        if (htmlFlowToFile)
                        {
                            // html tag to link to by msg start index number
                            flowFileWriter.Write("<a name = \"" + selectedmessages[i][0] + "\">");
                            flowFileWriter.WriteLine(WebUtility.HtmlEncode(line));
                        }
                        else
                        {
                            flowFileWriter.WriteLine(line);
                        }
                        // from read streamData to write file line of message to end index
                        for (int j = Int32.Parse(selectedmessages[i][0]) - 1; j < Int32.Parse(selectedmessages[i][9]) - 1; j++)
                        {
                            if (htmlFlowToFile)
                            {
                                flowFileWriter.WriteLine(WebUtility.HtmlEncode(streamData[j]));
                            }
                            else
                            {
                                flowFileWriter.WriteLine(streamData[j]);
                            }
                        }
                        if (htmlFlowToFile)
                        {
                            flowFileWriter.Write("</a>");
                            flowFileWriter.Write("<a href= \"#flow" + selectedmessages[i][0] + "\">Back</a>");
                            flowFileWriter.WriteLine("</br>");
                            flowFileWriter.WriteLine("<hr>");
                            flowFileWriter.WriteLine("</br>");
                        }
                    }
                    if (htmlFlowToFile)
                    {
                        flowFileWriter.Write("</div>");
                        flowFileWriter.WriteLine("</pre>");
                        flowFileWriter.WriteLine("</body>");
                        flowFileWriter.WriteLine("</html>");
                    }
                    htmlFlowToFile = false;
                    writeFlowToFile = false;
                    flowFileWriter.Close();
                }
                Flow(false);  //display call flow Diagram
            }
            else if (keypress.Key == ConsoleKey.H)
            {
                lock (_DisplayLocker)
                {
                    Console.ForegroundColor = msgBoxTxt;
                    Console.BackgroundColor = msgBoxBkgrd;
                    int center = Math.Max(0, (int)Math.Floor((decimal)((Console.WindowWidth - 64) / 2)));
                    Console.CursorLeft = center; Console.WriteLine(@"+------------------------------------------------------------+\ ");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Key                                                       | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Down Arrow / Page Down ----------------- move cursor down | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Up Arrow / Page Up ----------------------- move cursor up | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Spacebar / Enter ----------------------- view ISP message | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Enter ------------------------- Show diagram of call flow | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  Esc ----------------------- Exit to the list of call legs | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  H -------------------------------------- This help dialog | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  D -- Show SIP messages where  Src and Dst IP are the same | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  O --------------------------- Write the diagram to a file | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|  T -------------------------- Toggle local, UTC, timestamp | |");
                    Console.CursorLeft = center; Console.WriteLine(@"|                                                            | |");
                    Console.CursorLeft = center; Console.WriteLine(@"+------------------------------------------------------------+ |");
                    Console.CursorLeft = center; Console.WriteLine(@" \____________________________________________________________\|");
                    Console.BackgroundColor = fieldConsoleBkgrdClr;  //change the colors of the current postion to normal
                    Console.ForegroundColor = fieldConsoleTxtClr;
                    Console.ReadKey(true);
                    Flow(false);  //display call flow Diagram
                }
            }
            else if (keypress.Key == ConsoleKey.T)
            {
                if (timeMode == TZmode.stamp)
                {
                    timeMode = TZmode.local;
                }
                else
                {
                    timeMode++;
                }
                Flow(false);
            }
        }
        return;
    }

    void Flow(bool liveUpdate){
        SelectMessages();
        if (selectedmessages.Count == 0)
        {
            Log("No Messages were found when trying to render the flow", logFileSW);
            return;
        }
        GetIps();              //get the IP addresses of the selected SIP messages for the top of the screen  and addedto the IPsOfIntrest 
        if (liveUpdate && selectedmessages.Count > prevNumSelectMsg && IPsOfIntrest.Count == prevNumSelectdIPs) {
            if (selectedmessages.Count > Console.BufferHeight) {
                Console.BufferHeight = Math.Max(Math.Min(10 + selectedmessages.Count, Int16.MaxValue - 1), Console.BufferHeight);
            }
            for (int i = prevNumSelectMsg; i < selectedmessages.Count; i++){
                fakeCursor[0] = 0; fakeCursor[1] = i + 4;
                WriteMessageLine(selectedmessages[i], false);
                WriteLineConsole(new String('-', flowWidth - 1), fieldAttrTxtClr, fieldAttrBkgrdClr);
            }
            prevNumSelectMsg = selectedmessages.Count;
        }
        else {
            prevNumSelectMsg = selectedmessages.Count;
            prevNumSelectdIPs = IPsOfIntrest.Count;
            Console.BackgroundColor = fieldConsoleBkgrdClr;
            Console.ForegroundColor = fieldConsoleTxtClr;
            if (!liveUpdate) { ClearConsoleNoTop(); }
            Console.SetCursorPosition(0, 1);
            fakeCursor[0] = 0; fakeCursor[1] = 1;
            if (selectedmessages.Count > Console.BufferHeight){
                Console.BufferHeight = Math.Max(Math.Min(10 + selectedmessages.Count, Int16.MaxValue - 1), Console.BufferHeight);
            }
            flowWidth = 24;
            if (writeFlowToFile){
                if (htmlFlowToFile) {
                    flowFileWriter.Write("<div class=\"hosts\">");
                    flowFileWriter.Write("<br>");
                }
                WriteConsole(new String(' ', 17), fieldAttrTxtClr, fieldAttrBkgrdClr);
            }
            else {
                WriteConsole("[H]-Help"+ new String(' ', 9), fieldAttrTxtClr, fieldAttrBkgrdClr);
            }
            foreach (string ip in IPsOfIntrest) {
                flowWidth = flowWidth + 29;
                if (flowWidth > Console.BufferWidth){
                    Console.BufferWidth = Math.Min(15 + flowWidth, Int16.MaxValue - 1);
                }
                WriteConsole(ip + new String(' ', 29 - ip.Length), fieldAttrTxtClr, fieldAttrBkgrdClr);
            }
            WriteLineConsole("", fieldAttrTxtClr, fieldAttrBkgrdClr);
            string timeModeString = "";
            switch (timeMode){
                case TZmode.local: {
                        timeModeString = "Local Time";
                        break;
                    }
                case TZmode.utc:{
                        timeModeString = "UTC Time  ";
                        break;
                    }
                case TZmode.stamp:{
                        timeModeString = "Time Stamp";
                        break;
                    }
            }
            WriteConsole(timeModeString + new String(' ', 7), fieldAttrTxtClr, fieldAttrBkgrdClr);
            foreach (string ip in IPsOfIntrest){
                string ua = "";
                foreach (string[] ary in selectedmessages) {
                    if (ary[3] == ip && ary[16] != null) {
                        ua = ary[16].Substring(0, Math.Min(15, ary[16].Length));
                        break;
                    }
                }
                WriteConsole(ua + new String(' ', 29 - ua.Length), fieldAttrTxtClr, fieldAttrBkgrdClr);
            }
            WriteLineConsole("", fieldAttrTxtClr, fieldAttrBkgrdClr);
            WriteLineConsole(new String('-', flowWidth - 1), fieldAttrTxtClr, fieldAttrBkgrdClr);
            if (htmlFlowToFile){
                flowFileWriter.Write("</div>");
            }
            if (htmlFlowToFile) {
                flowFileWriter.Write("<div class=\"main\">");
            }
            foreach (string[] msg in selectedmessages) {
                WriteMessageLine(msg, false);
            }
            WriteLineConsole(new String('-', flowWidth - 1), fieldAttrTxtClr, fieldAttrBkgrdClr);
            if (flowSelectPosition > 17) { Console.SetWindowPosition(0, 0); }
            Console.SetCursorPosition(0, flowSelectPosition + 4);
            MessageLine(selectedmessages[flowSelectPosition], true);
            Console.CursorTop -= 1;
        }
    }

    void SelectMessages()
    {
        selectedmessages.Clear();
        lock (_DataLocker) for (int i = 0; i < messages.Count; i++)
            {
                if (callIDsOfIntrest.Contains(messages[i][6]))
                {
                    if (messages[i][3] != messages[i][4])
                    {
                        selectedmessages.Add(messages[i]);
                    }
                    else if (dupIP)
                    {
                        selectedmessages.Add(messages[i]);
                    }
                }
            }
        
        CallLegColors callcolor = CallLegColors.Green;
        lock (_DataLocker) foreach (string cid in callIDsOfIntrest)
            {
                for (int i = 0; i < selectedmessages.Count; i++)
                {
                    if (cid == selectedmessages[i][6])
                    {
                        selectedmessages[i][10] = callcolor.ToString();     //set color to display the call leg in the flow color for each call id
                    }
                }
                if (callcolor == CallLegColors.DarkMagenta) { callcolor = CallLegColors.Green; } else { callcolor++; }
            }
    }

    void GetIps()
    {
        if (IPprevNumSelectMsg != selectedmessages.Count)
        {
            IPsOfIntrest.Clear();
            IPprevNumSelectMsg = selectedmessages.Count;
            for (int i = 0; i < selectedmessages.Count; i++)
            {
                if (!IPsOfIntrest.Contains(selectedmessages[i][3]))
                {
                    IPsOfIntrest.Add(selectedmessages[i][3]);
                }
                if (!IPsOfIntrest.Contains(selectedmessages[i][4]))
                {
                    IPsOfIntrest.Add(selectedmessages[i][4]);
                }
            }
        }
    }

    void MessageLine(string[] message, bool invert){
        //get the index of the src and dst IP
        int srcindx = IPsOfIntrest.IndexOf(message[3]);
        int dstindx = IPsOfIntrest.IndexOf(message[4]);
        bool isright = false;
        int lowindx = 0;
        int hiindx = 0;
        string dateTimeString = "";
        switch (timeMode) {
            case TZmode.local:{
                    dateTimeString = DateTime.Parse(message[2]).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.ff", CultureInfo.InvariantCulture);
                    break;
                }
            case TZmode.utc:{
                    dateTimeString = DateTime.Parse(message[2]).ToString("yyyy-MM-dd HH:mm:ss.ff", CultureInfo.InvariantCulture);
                    break;
                }
            case TZmode.stamp: {
                    dateTimeString = DateTime.Parse(message[1]).ToString("yyyy-MM-dd HH:mm:ss.ff", CultureInfo.InvariantCulture);
                    break;
                }
        }
        if (srcindx == dstindx) {
            string firstline = message[5].Replace("SIP/2.0 ", "");
            string displayedline = firstline.Substring(0, Math.Min(18, firstline.Length)) + message[11];
            string space = new String(' ', 28) + "|";
            if (srcindx == 0){
                string spaceRight = new String(' ', 28 - (int)(Math.Ceiling((decimal)(displayedline.Length / 2)))) + "|";
                if (invert){
                    Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;
                    Console.ForegroundColor = fieldConsoleTxtInvrtClr;
                }
                else{
                    Console.BackgroundColor = fieldConsoleBkgrdClr;
                    Console.ForegroundColor = fieldConsoleTxtClr;
                }
                Console.Write("{0,-10}", dateTimeString);
                Console.ForegroundColor = (ConsoleColor)Enum.Parse(typeof(ConsoleColor), message[10]);
                Console.Write(displayedline + "<-");
                if (invert) {
                    Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;
                    Console.ForegroundColor = fieldConsoleTxtInvrtClr;
                }
                else {
                    Console.BackgroundColor = fieldConsoleBkgrdClr;
                    Console.ForegroundColor = fieldConsoleTxtClr;
                }
                Console.Write(new String(' ', 29 - (displayedline.Length + 2)) + "|");
                for (int i = 2; i < IPsOfIntrest.Count; i++){
                    Console.Write(space);
                }
            }
            else{
                string spaceLeft = new String(' ', 26 - (int)(Math.Floor((decimal)(displayedline.Length / 2))));                
                string spaceRight = new String(' ', 53 - spaceLeft.Length- displayedline.Length) + "|";
                if (invert){
                    Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;
                    Console.ForegroundColor = fieldConsoleTxtInvrtClr;
                }
                else{
                    Console.BackgroundColor = fieldConsoleBkgrdClr;
                    Console.ForegroundColor = fieldConsoleTxtClr;
                }
                Console.Write("{0,-10}|", dateTimeString);
                for (int i = 0; i < srcindx - 1; i++){
                    Console.Write(space);
                }
                Console.Write(spaceLeft);
                Console.ForegroundColor = (ConsoleColor)Enum.Parse(typeof(ConsoleColor), message[10]);
                Console.Write("->" + displayedline + "<-");
                if (invert){
                    Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;
                    Console.ForegroundColor = fieldConsoleTxtInvrtClr;
                }
                else{
                    Console.BackgroundColor = fieldConsoleBkgrdClr;
                    Console.ForegroundColor = fieldConsoleTxtClr;
                }
                if (srcindx < IPsOfIntrest.Count - 1){
                    Console.Write(spaceRight);
                    for (int i = srcindx + 2; i < IPsOfIntrest.Count; i++){
                        Console.Write(space);
                    }
                }
            }
        }
        else {
            string space = new String(' ', 28) + "|";
            if (srcindx < dstindx){
                lowindx = srcindx;
                hiindx = dstindx;
                isright = true;
            }
            if (srcindx > dstindx){
                lowindx = dstindx;
                hiindx = srcindx;
                isright = false;
            }
            if (invert){
                Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;
                Console.ForegroundColor = fieldConsoleTxtInvrtClr;
            }
            else{
                Console.BackgroundColor = fieldConsoleBkgrdClr;
                Console.ForegroundColor = fieldConsoleTxtClr;
            }
            Console.Write("{0,-10}|", dateTimeString);
            for (int i = 0; i < lowindx; i++) {
                Console.Write(space);
            }
            Console.ForegroundColor = (ConsoleColor)Enum.Parse(typeof(ConsoleColor), message[10]);
            if (isright) { Console.Write("-"); }
            else { Console.Write("<"); }
            string firstline = message[5].Replace("SIP/2.0 ", "");
            string displayedline = firstline.Substring(0, Math.Min(18, firstline.Length)) + message[11];
            int fullline = 29 * (hiindx - (lowindx + 1));
            double leftline = ((26 - displayedline.Length) + fullline) / 2; //
            Console.Write(new String('-', (int)Math.Floor(leftline)));
            Console.Write(displayedline);
            double rightline = 26 - leftline - displayedline.Length + fullline;
            Console.Write(new String('-', (int)rightline));
            if (isright) { Console.Write(">"); }
            else { Console.Write("-"); }
            if (invert){
                Console.BackgroundColor = fieldConsoleBkgrdInvrtClr;
                Console.ForegroundColor = fieldConsoleTxtInvrtClr;
            }
            else{
                Console.BackgroundColor = fieldConsoleBkgrdClr;
                Console.ForegroundColor = fieldConsoleTxtClr;
            }
            Console.Write("|");

            for (int i = 0; i < IPsOfIntrest.Count - 1 - hiindx; i++){
                Console.Write(space);
            }
        }
        if (message[13] != null) { Console.Write(" {0}:{1} {2}", message[13], message[14], message[15]); }
        Console.BackgroundColor = fieldConsoleBkgrdClr;
        Console.ForegroundColor = fieldConsoleTxtClr;
        Console.WriteLine();
    }

    void WriteMessageLine(string[] message, bool invert)
    {
        AttrColor TxtColor;
        AttrColor BkgrdColor;
        AttrColor CallTxtColor;
        //get the index of the src and dst IP
        int srcindx = IPsOfIntrest.IndexOf(message[3]);
        int dstindx = IPsOfIntrest.IndexOf(message[4]);
        bool isright = false;
        int lowindx = 0;
        int hiindx = 0;
        string dateTimeString = "";
        switch (timeMode){
            case TZmode.local:{
                    dateTimeString = DateTime.Parse(message[2]).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.ff", CultureInfo.InvariantCulture);
                    break;
                }
            case TZmode.utc:{
                    dateTimeString = DateTime.Parse(message[2]).ToString("yyyy-MM-dd HH:mm:ss.ff", CultureInfo.InvariantCulture);
                    break;
                }
            case TZmode.stamp:{
                    dateTimeString = DateTime.Parse(message[1]).ToString("yyyy-MM-dd HH:mm:ss.ff", CultureInfo.InvariantCulture);
                    break;
                }
        }
        if (srcindx == dstindx){
            string firstline = message[5].Replace("SIP/2.0 ", "");
            string displayedline = firstline.Substring(0, Math.Min(18, firstline.Length)) + message[11];
            string space = new String(' ', 28) + "|";
            if (srcindx == 0){
                string spaceRight = new String(' ', 28 - (int)(Math.Ceiling((decimal)(displayedline.Length / 2)))) + "|";
                if (invert){
                    TxtColor = fieldAttrTxtInvrtClr;
                    BkgrdColor = fieldAttrBkgrdInvrtClr;
                }
                else {
                    TxtColor = fieldAttrTxtClr;
                    BkgrdColor = fieldAttrBkgrdClr;
                }
                string formatedStr = String.Format("{0,-10}", dateTimeString);
                WriteConsole(formatedStr, TxtColor, BkgrdColor);
                CallTxtColor = (AttrColor)Enum.Parse(typeof(AttrColor), message[10]);
                if (htmlFlowToFile){
                    flowFileWriter.Write("<a name = \"flow" + message[0] + "\" href= \"#" + message[0] + "\">");
                }
                WriteConsole(displayedline + "<-", CallTxtColor, BkgrdColor);
                if (htmlFlowToFile){
                    flowFileWriter.Write("</a>");
                }
                WriteConsole(new String(' ', 29 - (displayedline.Length + 2)) + "|", TxtColor, BkgrdColor);
                for (int i = 2; i < IPsOfIntrest.Count; i++){
                    WriteConsole(space, TxtColor, BkgrdColor);
                }
            }
            else{
                string spaceLeft = new String(' ', 26 - (int)(Math.Floor((decimal)(displayedline.Length / 2))));                
                string spaceRight = new String(' ', 53 - spaceLeft.Length- displayedline.Length) + "|";
                if (invert) {
                    TxtColor = fieldAttrTxtInvrtClr;
                    BkgrdColor = fieldAttrBkgrdInvrtClr;
                }
                else {
                    TxtColor = fieldAttrTxtClr;
                    BkgrdColor = fieldAttrBkgrdClr;
                }
                string formatedStr = String.Format("{0,-10}|", dateTimeString);
                WriteConsole(formatedStr, TxtColor, BkgrdColor);
                for (int i = 0; i < srcindx - 1; i++){
                    WriteConsole(space, TxtColor, BkgrdColor);
                }
                WriteConsole(spaceLeft, TxtColor, BkgrdColor);
                CallTxtColor = (AttrColor)Enum.Parse(typeof(AttrColor), message[10]);
                if (htmlFlowToFile) {
                    flowFileWriter.Write("<a name = \"flow" + message[0] + "\" href= \"#" + message[0] + "\">");
                }
                WriteConsole("->" + displayedline + "<-", CallTxtColor, BkgrdColor);
                if (htmlFlowToFile){
                    flowFileWriter.Write("</a>");
                }
                if (srcindx < IPsOfIntrest.Count - 1){
                    WriteConsole(spaceRight, TxtColor, BkgrdColor);
                    for (int i = srcindx + 2; i < IPsOfIntrest.Count; i++){
                        WriteConsole(space, TxtColor, BkgrdColor);
                    }
                }
            }
        }
        else{
            string space = new String(' ', 28) + "|";
            if (srcindx < dstindx){
                lowindx = srcindx;
                hiindx = dstindx;
                isright = true;
            }
            if (srcindx > dstindx){
                lowindx = dstindx;
                hiindx = srcindx;
                isright = false;
            }
            if (invert){
                TxtColor = fieldAttrTxtInvrtClr;
                BkgrdColor = fieldAttrBkgrdInvrtClr;
            }
            else{
                TxtColor = fieldAttrTxtClr;
                BkgrdColor = fieldAttrBkgrdClr;
            }
            string formatedStr = String.Format("{0,-10}|", dateTimeString);
            WriteConsole(formatedStr, TxtColor, BkgrdColor);
            for (int i = 0; i < lowindx; i++){
                WriteConsole(space, TxtColor, BkgrdColor);
            }
            CallTxtColor = (AttrColor)Enum.Parse(typeof(AttrColor), message[10]);
            if (htmlFlowToFile) {
                flowFileWriter.Write("<a name = \"flow" + message[0] + "\" href= \"#" + message[0] + "\">");
            }
            if (isright) { WriteConsole("-", CallTxtColor, BkgrdColor); }
            else { WriteConsole("<", CallTxtColor, BkgrdColor); }
            string firstline = message[5].Replace("SIP/2.0 ", "");
            string displayedline = firstline.Substring(0, Math.Min(18, firstline.Length)) + message[11];
            int fullline = 29 * (hiindx - (lowindx + 1));
            double leftline = ((26 - displayedline.Length) + fullline) / 2; //
            WriteConsole(new String('-', (int)Math.Floor(leftline)), CallTxtColor, BkgrdColor);
            WriteConsole(displayedline, CallTxtColor, BkgrdColor);
            double rightline = 26 - leftline - displayedline.Length + fullline;
            WriteConsole(new String('-', (int)rightline), CallTxtColor, BkgrdColor);
            if (isright) { WriteConsole(">", CallTxtColor, BkgrdColor); }
            else { WriteConsole("-", CallTxtColor, BkgrdColor); }
            if (htmlFlowToFile) {
                flowFileWriter.Write("</a>");
            }
            WriteConsole("|", TxtColor, BkgrdColor);
            for (int i = 0; i < IPsOfIntrest.Count - 1 - hiindx; i++) {
                WriteConsole(space, TxtColor, BkgrdColor);
            }
        }
        if (message[13] != null) {
            String AnotherFrmtStr = String.Format(" {0}:{1} {2}", message[13], message[14], message[15]);
            WriteConsole(AnotherFrmtStr, TxtColor, BkgrdColor);
        }
        WriteLineConsole("", TxtColor, BkgrdColor);
    }

    void DisplayMessage(int msgindxselected, List<string[]> inputMessages){
        int msgStartIdx = Int32.Parse(inputMessages[msgindxselected][0]);
        int msgEndIdx = Int32.Parse(inputMessages[msgindxselected][9]);
        if ((msgEndIdx - msgStartIdx) > Console.BufferHeight) {
            Console.BufferHeight = Math.Max(Math.Min(5 + (Int16)(msgEndIdx - msgStartIdx), Int16.MaxValue - 1), Console.BufferHeight);
        }
        ClearConsoleNoTop();
        Console.SetCursorPosition(0, 1);
        fakeCursor[0] = 0; fakeCursor[1] = 1;
        string line = "";
        if (writeFlowToFile) {
            flowFileWriter.Write("<a name = \"" + inputMessages[msgindxselected][0] + "\">");
            flowFileWriter.WriteLine(line);
        }
        else{
            Console.WriteLine();
        }
        for (int j = msgStartIdx - 1; j < msgEndIdx - 1; j++) {
            if (writeFlowToFile) {
                flowFileWriter.WriteLine(streamData[j]);
            }
            else {
                Console.WriteLine(streamData[j]);
            }
        }
        if (writeFlowToFile){
            flowFileWriter.Write("</a>");
            flowFileWriter.Write("<a href= \"#flow" + inputMessages[msgindxselected][0] + "\">Back</a>");
            flowFileWriter.WriteLine("</br>");
            flowFileWriter.WriteLine("<hr>");
            flowFileWriter.WriteLine("</br>");
        }
        Console.SetCursorPosition(0, 1);
        fakeCursor[0] = 0; fakeCursor[1] = 1;
        ConsoleKeyInfo keypressed;
        while (!writeFlowToFile && !((keypressed = Console.ReadKey(true)).Key == ConsoleKey.Escape)){
            if (Console.CursorTop < Console.BufferHeight - 1 && keypressed.Key == ConsoleKey.DownArrow) {
                Console.CursorTop++;
            }
            if (Console.CursorTop > 0 && keypressed.Key == ConsoleKey.UpArrow) {
                Console.CursorTop--;
            }
        }
    }

    void ListAllMsg(string regexStr) {
        List<string[]> filtered = new List<string[]>();
        bool done = false;
        int position = 0;
        int MsgLineLen;
        int match = 0;
        string strginput;
        ClearConsoleNoTop();
        if (regexStr == null) {
            Console.SetCursorPosition(0, 1);
            Console.WriteLine("Enter regex to search. Max lines displayed are 32765. example: for all the msg to/from 10.28.160.42 at 16:40:11 use 16:40:11.*10.28.160.42");
            Console.WriteLine("Data format: line number|date|time|src IP|dst IP|first line of SIP msg|From:|To:|Call-ID|line number|color|has SDP|filename|SDP IP|SDP port|SDP codec|useragent|cseq");
            strginput = Console.ReadLine();
        }
        else {
            strginput = regexStr;
        }
        ClearConsoleNoTop();
        Console.SetCursorPosition(0, 1);
        fakeCursor[0] = 0; fakeCursor[1] = 1;
        if (string.IsNullOrEmpty(strginput)) {
            Console.WriteLine("You must enter a regex");
            Console.ReadKey(true);
            done = true;
        }
        else {
            Regex regexinput = new Regex(strginput);
            lock (_DataLocker) for (int i = 0; i < Math.Min(messages.Count, Int16.MaxValue - 2); i++) {
                string[] ary = messages[i];
                if (regexinput.IsMatch(string.Join(" ", ary))){
                    match++;
                    if (match + 1 > Console.BufferHeight){
                        if (match < Int16.MaxValue - 100 + 1){
                            Console.BufferHeight = match + 100 + 1;
                        }
                        else {
                            Console.BufferHeight = match + 1;
                        }
                    }
                    MsgLineLen = string.Join(" ", ary).Length + 28;
                    if (MsgLineLen >= Console.BufferWidth) { Console.BufferWidth = MsgLineLen + 1; }
                    WriteLineConsole(String.Format("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", ary), fieldAttrTxtClr, fieldAttrBkgrdClr);
                    filtered.Add(ary);
                }
            }
            if (filtered.Count == 0) {
                Console.WriteLine("NO search matches found. Press any key to continue");
                Console.ReadKey(true);
                return;
            }
            Console.SetCursorPosition(0, 1);
            Console.BackgroundColor = fieldConsoleTxtClr;
            Console.ForegroundColor = fieldConsoleBkgrdClr;
            Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
            Console.CursorTop -= 1;
            Console.BackgroundColor = fieldConsoleBkgrdClr;
            Console.ForegroundColor = fieldConsoleTxtClr;
        }
        while (!done) {
            ConsoleKeyInfo keypressed = Console.ReadKey(true);
            switch (keypressed.Key) {
                case ConsoleKey.DownArrow:
                    if (position < filtered.Count - 1) {
                        Console.BackgroundColor = fieldConsoleBkgrdClr;
                        Console.ForegroundColor = fieldConsoleTxtClr;
                        Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                        position++;
                        Console.BackgroundColor = fieldConsoleTxtClr;
                        Console.ForegroundColor = fieldConsoleBkgrdClr;
                        Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                        Console.CursorTop -= 1;
                        Console.BackgroundColor = fieldConsoleBkgrdClr;
                        Console.ForegroundColor = fieldConsoleTxtClr;
                    }
                    break;

                case ConsoleKey.PageDown:
                    if (position + 40 < filtered.Count - 1){
                        Console.BackgroundColor = fieldConsoleBkgrdClr;
                        Console.ForegroundColor = fieldConsoleTxtClr;
                        Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                        Console.CursorTop += 39;
                        position += 40;
                        Console.BackgroundColor = fieldConsoleTxtClr;
                        Console.ForegroundColor = fieldConsoleBkgrdClr;
                        Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                        Console.CursorTop -= 1;
                        Console.BackgroundColor = fieldConsoleBkgrdClr;
                        Console.ForegroundColor = fieldConsoleTxtClr;
                    }
                    break;

                case ConsoleKey.UpArrow:
                    if (position > 0)  {
                        Console.BackgroundColor = fieldConsoleBkgrdClr;
                        Console.ForegroundColor = fieldConsoleTxtClr;
                        Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                        position--;
                        Console.CursorTop -= 2;
                        Console.BackgroundColor = fieldConsoleTxtClr;
                        Console.ForegroundColor = fieldConsoleBkgrdClr;
                        Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                        Console.CursorTop -= 1;
                        Console.BackgroundColor = fieldConsoleBkgrdClr;
                        Console.ForegroundColor = fieldConsoleTxtClr;
                    }
                    break;
                case ConsoleKey.PageUp:
                    if (position > 39)
                    {
                        Console.BackgroundColor = fieldConsoleBkgrdClr;
                        Console.ForegroundColor = fieldConsoleTxtClr;
                        Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                        position -= 40;
                        Console.CursorTop -= 41;
                        Console.BackgroundColor = fieldConsoleTxtClr;
                        Console.ForegroundColor = fieldConsoleBkgrdClr;
                        Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                        Console.CursorTop -= 1;
                        Console.BackgroundColor = fieldConsoleBkgrdClr;
                        Console.ForegroundColor = fieldConsoleTxtClr;
                    }
                    break;

                case ConsoleKey.Enter:
                    DisplayMessage(position, filtered);
                    Console.Clear();
                    Console.SetCursorPosition(0, 0);
                    foreach (string[] line in filtered){
                        WriteLineConsole(String.Format("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", line), fieldAttrTxtClr, fieldAttrBkgrdClr);
                    }
                    Console.SetCursorPosition(0, position + 1);
                    Console.BackgroundColor = fieldConsoleTxtClr;
                    Console.ForegroundColor = fieldConsoleBkgrdClr;
                    Console.WriteLine("{0,-7}{1,-11}{2,-16}{3,-16}{4,-16}{5} From:{8} To:{7} {6} {9} {10} {11} {12} {13} {14} {15} {16} {17}", filtered[position]);
                    Console.CursorTop = Console.CursorTop - 1;
                    Console.BackgroundColor = fieldConsoleBkgrdClr;
                    Console.ForegroundColor = fieldConsoleTxtClr;
                    break;

                case ConsoleKey.Escape:
                    done = true;
                    break;
            }
        }
    }

    void TopLine(string line, short x) {
        string displayLine;
        if (line.Length > 1){
            displayLine = line + new String(' ', Math.Max(Console.BufferWidth - line.Length, 0));
        }
        else {
            displayLine = line;
        }
        lock (_DisplayLocker){
            ConsoleBuffer.SetAttribute(x, 0, line.Length, (short)(statusBarTxtClr + (short)(((short)statusBarBkgrdClr) * 16)));
            ConsoleBuffer.WriteAt(x, 0, displayLine);
        }        
    }

    void WriteScreen(string line, short x, short y, AttrColor attr, AttrColor bkgrd){
        ConsoleBuffer.SetAttribute(x, y, line.Length, (short)(attr + (short)(((short)bkgrd) * 16)));
        ConsoleBuffer.WriteAt(x, y, line);
    }

    void WriteConsole(string line, AttrColor attr, AttrColor bkgrd) {
        if (writeFlowToFile)  {
            if (htmlFlowToFile) {
                string htmlTxtClr;
                if (attr.ToString() == "Cyan"){
                    htmlTxtClr = "DarkTurquoise";
                }
                else if (attr.ToString() == "Yellow"){
                    htmlTxtClr = "GoldenRod";
                }
                else if (attr.ToString() == "DarkGreen"){
                    htmlTxtClr = "YellowGreen";
                }
                else if (attr.ToString() == "DarkCyan"){
                    htmlTxtClr = "CadetBlue";
                }
                else if (attr.ToString() == "Gray") {
                    htmlTxtClr = "Black";
                }
                else {
                    htmlTxtClr = attr.ToString();
                }
                flowFileWriter.Write("<code style = \"color:" + htmlTxtClr + ";\" >");
            }
            flowFileWriter.Write(line);
            if (htmlFlowToFile) { flowFileWriter.Write("</code>"); }
        }
        else {
            WriteScreen(line, (short)fakeCursor[0], (short)fakeCursor[1], attr, bkgrd);
            fakeCursor[0] = fakeCursor[0] + line.Length;
        }
    }

    void WriteLineConsole(string line, AttrColor attr, AttrColor bkgrd){
        if (writeFlowToFile) {
            flowFileWriter.WriteLine(line);
        }
        else{
            WriteConsole(line + new String(' ', Console.BufferWidth - line.Length)
            , attr, bkgrd);
            fakeCursor[1]++;
            fakeCursor[0] = 0;
        }
    }

    void ClearConsole() {
        bool iswriteFlowToFileTrue = writeFlowToFile;
        writeFlowToFile = false;
        int[] prevFakeCursor = new int[2];
        prevFakeCursor = fakeCursor;
        fakeCursor[0] = 0; fakeCursor[1] = 0;
        for (int i = 0; i < Console.BufferHeight; i++){
            WriteLineConsole("", fieldAttrTxtClr, fieldAttrBkgrdClr);
        }
        fakeCursor = prevFakeCursor;
        writeFlowToFile = iswriteFlowToFileTrue;
    }

    void ClearConsoleNoTop() {
        bool iswriteFlowToFileTrue = writeFlowToFile;
        writeFlowToFile = false;
        int[] prevFakeCursor = new int[2];
        prevFakeCursor = fakeCursor;
        fakeCursor[0] = 0; fakeCursor[1] = 1;
        for (int i = 0; i < Console.BufferHeight; i++){
            WriteLineConsole("", fieldAttrTxtClr, fieldAttrBkgrdClr);
        }
        fakeCursor = prevFakeCursor;
        writeFlowToFile = iswriteFlowToFileTrue;
    }

    String SecureStringToString(SecureString value) {
        IntPtr valuePtr = IntPtr.Zero;
        try {
            valuePtr = Marshal.SecureStringToGlobalAllocUnicode(value);
            return Marshal.PtrToStringUni(valuePtr);
        }
        finally{
            Marshal.ZeroFreeGlobalAllocUnicode(valuePtr);
        }
    }

    public static void Log(string logMessage, TextWriter w)
    {
        lock (_LogLocker)
        {
            w.Write("\r\nLog Entry : ");
            w.WriteLine("{0} {1}", DateTime.Now.ToLongTimeString(),
                DateTime.Now.ToLongDateString());
            w.WriteLine("  :");
            w.WriteLine("  :{0}", logMessage);
            w.WriteLine("-------------------------------");
        }
    }
}

public class ConsoleBuffer{
    private static SafeFileHandle _hBuffer = null;

    static ConsoleBuffer(){
        const int STD_OUTPUT_HANDLE = -11;
        _hBuffer = GetStdHandle(STD_OUTPUT_HANDLE);
        if (_hBuffer.IsInvalid) {
            throw new Exception("Failed to open console buffer");
        }
    }

    public static void WriteAt(short x, short y, string value) {
        int n = 0;
        WriteConsoleOutputCharacter(_hBuffer, value, value.Length, new Coord(x, y), ref n);
    }

    public static void SetAttribute(short x, short y, int length, short attr) {
        short[] attrAry = new short[length];
        for (int i = 0; i < length; i++) {
            attrAry[i] = attr;
        }
        SetAttribute(x, y, length, attrAry);
    }

    public static void SetAttribute(short x, short y, int length, short[] attrs){
        int n = 0;
        WriteConsoleOutputAttribute(_hBuffer, attrs, length, new Coord(x, y), ref n);
    }

    

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteConsoleOutput(
      SafeFileHandle hConsoleOutput,
      CharInfo[] lpBuffer,
      Coord dwBufferSize,
      Coord dwBufferCoord,
      ref SmallRect lpWriteRegion);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteConsoleOutputCharacter(
      SafeFileHandle hConsoleOutput,
      string lpCharacter,
      int nLength,
      Coord dwWriteCoord,
      ref int lpumberOfCharsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteConsoleOutputAttribute(
      SafeFileHandle hConsoleOutput,
      short[] lpAttributes,
      int nLength,
      Coord dwWriteCoord,
      ref int lpumberOfAttrsWritten);

    [StructLayout(LayoutKind.Sequential)]
    struct Coord {
        public short X;
        public short Y;
        public Coord(short X, short Y){
            this.X = X;
            this.Y = Y;
        }
    };

    [StructLayout(LayoutKind.Explicit)]
    struct CharUnion {
        [FieldOffset(0)]
        public char UnicodeChar;
        [FieldOffset(0)]
        public byte AsciiChar;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct CharInfo{
        [FieldOffset(0)]
        public CharUnion Char;
        [FieldOffset(2)]
        public short Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SmallRect{
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }
}


/*
index=siplog | 
rex field=_raw "(?<SIP_SrcIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(:|.)\d*(?= >))"|
rex field=_raw "(?<SIP_DstIP>(?<=> )\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(:|.)\d*)"|
rex field=_raw "(?<SIP_Req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))"|
rex field=_raw "(?<!-.{8})(?<=Call-ID:)\s*(?<SIP_CallId>.*)"|
rex field=_raw "(?<=To:) *(\x22.+\x22)? *<?(sip:)(?<SIP_To>[^@>]+)"|
rex field=_raw "(?<=From:) *(\x22.+\x22)? *<?(sip:)(?<SIP_From>[^@>]+)"|
rex field=SIP_Req "(?<SIP_method>^[a-zA-Z]+)"|
eval timeForamted=strftime(_time, "%Y-%m-%d %H:%M:%S.%6N%:z")|
eval UTC=""|
eval selected=""|
eval filtered=""|
search SIP_Req = *INVITE* OR SIP_Req =*NOTIFY* OR SIP_Req =*REGISTER* OR SIP_Req =*SUBSCRIBE*|
stats first(SIP_To) as To, first(SIP_From) as From, first(SIP_SrcIP) as Source_IP, first(SIP_DstIP) as Destination_IP, first(timeForamted)  as DateTime first(SIP_method) as Method by SIP_CallId| 
table DateTime,UTC,To,From,SIP_CallId,selected,Source_IP,Destination_IP,filtered,Method|
sort DateTime

    for inbound audiocods syslog to get the source IP from the previous event | rex field=_raw "(?<=Incoming SIP Message from)\s*(?<SrcIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d{1,6})" | reverse |streamstats current=f window=1 last(SrcIP) as prev_SrcIP
    for outbound 
    Sent:2018-06-18T09:21:44.916-04:00 Recv:2018-06-18T09:21:44.916-04:00 [local0] [notice] 10.232.244.20 [S=2409160] [SID=9610bc:11:1031159]  (      lgr_flow)(   2411058)   ---- Outgoing SIP Message to 10.232.244.10:5060 from SIPInterface #0 (SIPInterface_0) UdpTransportObject(#0)-UdpSocketAPI(#0) ----
    rex field=_raw "(?<=Outgoing SIP Message to)\s*(?<DstIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d{1,6})"
    
    index="eventsnap" source=*everything.log* 2779429 |rex field=_raw "\[.*\]\s*\[.*\]\s*(?<MGIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})" | rex field=_raw "(?<=Incoming SIP Message from)\s*(?<SrcIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d{1,6})" |rex field=_raw "(?<=Outgoing SIP Message to)\s*(?<DstIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d{1,6})"| reverse |streamstats current=f window=1 last(DstIP) as prev_DstIP last(SrcIP) as prev_SrcIP | eval SIP_dstIP=if(prev_DstIP != "",prev_DstIP,MGIP) | eval SIP_srcIP=if(prev_SrcIP != "",prev_SrcIP,MGIP)

index="eventsnap" source=*everything.log*|
rex field=_raw "(?<SIP_Req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))"|
rex field=_raw "(?<!-.{8})(?<=Call-ID:)\s*(?<SIP_CallId>.*)"|
rex field=SIP_Req "(?<SIP_method>^[a-zA-Z]+)"|
rex field=_raw "\[.*\]\s*\[.*\]\s*(?<MGIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})" | 
rex field=_raw "(?<=Incoming SIP Message from)\s*(?<SrcIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})" |
rex field=_raw "(?<=Outgoing SIP Message to)\s*(?<DstIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})"| 
reverse |streamstats current=f window=1 last(DstIP) as prev_DstIP last(SrcIP) as prev_SrcIP | 
eval SIP_dstIP=if(prev_DstIP != "",prev_DstIP,MGIP) | eval SIP_srcIP=if(prev_SrcIP != "",prev_SrcIP,MGIP) |
search SIP_Req = *INVITE* OR SIP_Req =*NOTIFY* OR SIP_Req =*REGISTER* OR SIP_Req =*SUBSCRIBE* | 
stats first(SIP_srcIP) as Source_IP, first(SIP_dstIP) as Destination_IP, first(_raw) as Raw by SIP_CallId | 
table Source_IP,Destination_IP,Raw | 
sort DateTime

index=acsbc2 |rex field=_raw "(?<SIP_Req>ACK.*SIP\/2\.0|BYE.*SIP\/2\.0|CANCEL.*SIP\/2\.0|INFO.*SIP\/2\.0|INVITE.*SIP\/2\.0|MESSAGE.*SIP\/2\.0|NOTIFY.*SIP\/2\.0|OPTIONS.*SIP\/2\.0|PRACK.*SIP\/2\.0|PUBLISH.*SIP\/2\.0|REFER.*SIP\/2\.0|REGISTER.*SIP\/2\.0|SUBSCRIBE.*SIP\/2\.0|UPDATE.*SIP\/2\.0|SIP\/2\.0 \d{3}(\s*\w*))"|
rex field=_raw "(?<!-.{8})(?<=Call-ID:)\s*(?<SIP_CallId>.*)"|
rex field=SIP_Req "(?<SIP_method>^[a-zA-Z]+)"|
rex field=_raw "\d{2}:\d{2}:\d{2}.\d{3}\s*(?<MGIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})" | 
rex field=_raw "(?<=Incoming SIP Message from)\s*(?<SrcIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})" |
rex field=_raw "(?<=Outgoing SIP Message to)\s*(?<DstIP>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})"| 
reverse |streamstats current=f window=1 last(DstIP) as prev_DstIP last(SrcIP) as prev_SrcIP | 
eval SIP_dstIP=if(prev_DstIP != "",prev_DstIP,MGIP) | eval SIP_srcIP=if(prev_SrcIP != "",prev_SrcIP,MGIP)

*/
