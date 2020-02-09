using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;
using System.Runtime.InteropServices;

namespace Client
{
    public partial class client : Form
    {
        MAVLink.MavlinkParse mavlink = new MAVLink.MavlinkParse();
        // locking to prevent multiple reads on serial port
        object readlock = new object();
        // our target sysid
        byte sysid;
        // our target compid
        byte compid;

        GMapOverlay markers = new GMapOverlay("markers");
        GMapOverlay lines = new GMapOverlay("lines");
        GMapMarker marker = new GMarkerGoogle(new PointLatLng(0, 0), new Bitmap(drone));

        public static Bitmap original_drone = new Bitmap(Properties.Resources.drone);
        public static Bitmap drone = new Bitmap(original_drone, new Size(original_drone.Width / 12, original_drone.Height / 12));
        
        public static Bitmap original_dot = new Bitmap(Properties.Resources.Basic_red_dot);
        public static Bitmap dot = new Bitmap(original_dot, new Size(original_dot.Width / 60, original_dot.Height / 60));

        bool half_battery_signal = false;

        public client()
        {
            InitializeComponent();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)]
        static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr w, IntPtr l);
        public static void SetState(ProgressBar pBar, int state)
        {
            SendMessage(pBar.Handle, 1040, (IntPtr)state, IntPtr.Zero);
        }

        private void but_connect_Click(object sender, EventArgs e)
        {
            try
            {
                // if the port is open close it
                if (serialPort1.IsOpen)
                {
                    serialPort1.Close();
                    marker.IsVisible = false;
                    return;
                }

                // set the comport options
                serialPort1.PortName = CMB_comport.Text;
                serialPort1.BaudRate = int.Parse(comboBox1.Text);

                // open the comport
                serialPort1.Open();

                // set timeout to 2 seconds
                serialPort1.ReadTimeout = 2000;

                BackgroundWorker bgw = new BackgroundWorker();

                bgw.DoWork += bgw_DoWork;

                bgw.RunWorkerAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("------------------------------------------------------------");
                Console.WriteLine(ex.ToString());
                Console.WriteLine("------------------------------------------------------------");
            }
        }

        public static DisplayAttribute GetDisplayAttributesFrom(Enum enumValue, Type enumType)
        {
            return enumType.GetMember(enumValue.ToString())
                           .First()
                           .GetCustomAttribute<DisplayAttribute>();
        }

        int mav_messages_counter = 0;
        void bgw_DoWork(object sender, DoWorkEventArgs e)
        {
            while (serialPort1.IsOpen)
            {
                try
                {
                    MAVLink.MAVLinkMessage packet;
                    lock (readlock)
                    {
                        // read any valid packet from the port
                        packet = mavlink.ReadPacket(serialPort1.BaseStream);

                        // check its valid
                        if (packet == null || packet.data == null)
                            continue;
                    }

                    // check to see if its a hb packet from the comport
                    if (packet.data.GetType() == typeof(MAVLink.mavlink_heartbeat_t))
                    {

                        var hb = (MAVLink.mavlink_heartbeat_t)packet.data;

                        // save the sysid and compid of the seen MAV
                        sysid = packet.sysid;
                        compid = packet.compid;

                        // request streams at 2 hz
                        var buffer = mavlink.GenerateMAVLinkPacket10(MAVLink.MAVLINK_MSG_ID.REQUEST_DATA_STREAM,
                            new MAVLink.mavlink_request_data_stream_t()
                            {
                                req_message_rate = 2,
                                req_stream_id = (byte)MAVLink.MAV_DATA_STREAM.ALL,
                                start_stop = 1,
                                target_component = compid,
                                target_system = sysid
                            });

                        serialPort1.Write(buffer, 0, buffer.Length);

                        buffer = mavlink.GenerateMAVLinkPacket10(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, hb);

                        serialPort1.Write(buffer, 0, buffer.Length);
                    }

                    // from here we should check the the message is addressed to us
                    if (sysid != packet.sysid || compid != packet.compid)
                        continue;

                    //Console.WriteLine(packet.msgtypename);

                    listBox1.Invoke(new Action(() =>
                    {

                        if (!listBox1.Items.Contains(packet.msgtypename))
                        {
                            mav_messages_counter++;
                            if (mav_messages_counter > 0) mav_messages_Label.ForeColor = Color.Green;
                            listBox1.Items.Add(packet.msgtypename);
                            mav_messages_Label.Text = "Found " + mav_messages_counter + " messages.";
                        }

                    }));


                    //detect gps packet
                    if (packet.msgid == (byte)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT)
                    {
                        marker.IsVisible = true;
                        var global_position_int = (MAVLink.mavlink_global_position_int_t)packet.data;
                        //get longitude and latitude info from packet
                        var lon = global_position_int.lon / 10000000.0;
                        var lat = global_position_int.lat / 10000000.0;
                        gmap.Invoke(new Action(() => //access gui thread
                        {
                            //draw on map
                            gmap.Position = new PointLatLng(lat, lon);
                            GMapMarker line = new GMarkerGoogle(new PointLatLng(lat, lon), new Bitmap(dot));
                            marker.Position = new PointLatLng(lat, lon);
                            lines.Markers.Add(line);
                            gmap.Overlays.Add(lines);
                        }));

                    }

                    //detect system status packet
                    if (packet.msgid == (byte)MAVLink.MAVLINK_MSG_ID.SYS_STATUS)
                    {
                        progressBar1.Invoke(new Action(() =>
                         {
                             var sys_status = (MAVLink.mavlink_sys_status_t)packet.data;
                             //get battery remaining
                             if (sys_status.battery_remaining < 15)
                             {
                                 SetState(progressBar1, 2);
                                 Console.Beep(600, 100);
                             }
                             else if (sys_status.battery_remaining < 50)
                             {
                                 if (!half_battery_signal)
                                 {
                                     SetState(progressBar1, 3);
                                     half_battery_signal = true;
                                     Console.Beep(600, 1000);
                                 }
                             }
                             else
                             {
                                 SetState(progressBar1, 1);
                             }
                             progressBar1.Value = sys_status.battery_remaining;
                         }));
                    }

                    //get heartbeat packet
                    if (packet.msgid == (byte)MAVLink.MAVLINK_MSG_ID.HEARTBEAT)
                        {
                            var heartbeat = (MAVLink.mavlink_heartbeat_t)packet.data;
                            txt_vehicleType.Invoke(new Action(() => //access gui thread
                            {
                                //print information
                                txt_vehicleType.Text = ((MAVLink.MAV_TYPE)heartbeat.autopilot).ToString();
                                txt_autopilot.Text = ((MAVLink.MAV_AUTOPILOT)heartbeat.autopilot).ToString();
                                txt_baseMode.Text = ((MAVLink.MAV_MODE_FLAG)heartbeat.base_mode).ToString();
                                txt_system.Text = ((MAVLink.MAV_STATE)heartbeat.system_status).ToString();
                            }));
                        }

                    
                }
                catch (Exception ex)
                {
                   Console.WriteLine("--------------------------------------");
                   Console.WriteLine(ex.ToString());
                   Console.WriteLine("--------------------------------------");
                }

                System.Threading.Thread.Sleep(1);
            }
        }

        T readsomedata<T>(byte sysid, byte compid, int timeout = 2000)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeout);

            lock (readlock)
            {
                // read the current buffered bytes
                while (DateTime.Now < deadline)
                {
                    var packet = mavlink.ReadPacket(serialPort1.BaseStream);

                    // check its not null, and its addressed to us
                    if (packet == null || sysid != packet.sysid || compid != packet.compid)
                        continue;

                    //Console.WriteLine(packet);

                    if (packet.data.GetType() == typeof(T))
                    {
                        return (T)packet.data;
                    }
                }
            }

            throw new Exception("No packet match found");
        }


        private void CMB_comport_Click(object sender, EventArgs e)
        {
            CMB_comport.DataSource = SerialPort.GetPortNames();
        }


        private void client_Load(object sender, EventArgs e)
        {
            //init

            //init gmap element
            gmap.MapProvider = GMap.NET.MapProviders.GMapProviders.GoogleHybridMap;
            GMaps.Instance.Mode = AccessMode.ServerOnly;

            gmap.ShowCenter = true;
            marker.IsVisible = false;
            //init draw markers
            markers.Markers.Add(marker);
            gmap.Overlays.Add(markers);
        }

        private void button1_Click(object sender, EventArgs e){
            add_module add_module = new add_module();
            add_module.Show();
        }
       }
}