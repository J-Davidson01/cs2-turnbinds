using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using cs2_turnbinds;

public class Config
{
    public List<int> Yaw { get; set; }
    public double Sensitivity { get; set; }
    public double M_Yaw { get; set; }
    public Keys Left { get; set; }
    public Keys Right { get; set; }
    public Keys Toggle { get; set; }
    public Keys Pause { get; set; }
    public Keys Inc { get; set; }
    public Keys Dec { get; set; }
    public Keys Exit { get; set; }
}

[JsonSerializable(typeof(Config))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, Converters = new[] {typeof(KeysConverter)})]
public partial class ConfigJsonContext : JsonSerializerContext
{
}

public class KeysConverter : JsonConverter<Keys>
{
    public override Keys Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string keyName = reader.GetString();
        // try parse with _ prefix
        if (Enum.TryParse(typeof(Keys), "_" + keyName, out var result2))
        {
            return (Keys) result2;
        }
        
        // try parse
        if (Enum.TryParse(typeof(Keys), keyName, out var result))
        {
            return (Keys) result;
        }
       
        
        throw new JsonException($"Invalid key name: {keyName}");
    }

    public override void Write(Utf8JsonWriter writer, Keys value, JsonSerializerOptions options)
    {
        var str = value.ToString();
        // replace _ prefix with nothing
        if (str.StartsWith("_"))
        {
            writer.WriteStringValue(str.Substring(1));
        }
        else
        {
            writer.WriteStringValue(str);
        }
    }
}

class Program
{
    [StructLayout(LayoutKind.Explicit)]
    struct LARGE_INTEGER
    {
        [FieldOffset(0)] public long QuadPart;
    }
    
    
    [DllImport("user32.dll")]
    static extern void mouse_event(int dwFlags, int dx, int dy, int dwData, int dwExtraInfo);
    
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    
    [DllImport("ntdll.dll")]
    private static extern uint NtDelayExecution([In] bool Alertable, [In] ref LARGE_INTEGER DelayInterval);
    
    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);
    
    // [DllImport("ntdll.dll")]
    // private static extern int NtQueryTimerResolution(out uint MinimumResolution, out uint MaximumResolution, out uint CurrentResolution);
    
    
    const int MOUSEEVENTF_MOVE = 0x0001;
    const int MOUSEEVENTF_ABSOLUTE = 0x8000;
    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;

    static Process? foregroundProcess;
    static uint foregroundProcessId;

    static bool IsCs2Foreground()
    {
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var processId);

        try
        {
            if (processId != foregroundProcessId || foregroundProcess == null)
            {
                foregroundProcess?.Dispose();
                foregroundProcess = null;
                foregroundProcessId = 0;

                if (window == IntPtr.Zero || processId == 0)
                {
                    return false;
                }

                foregroundProcess = Process.GetProcessById(checked((int)processId));
                foregroundProcessId = processId;
            }

            return !foregroundProcess.HasExited &&
                string.Equals(foregroundProcess.ProcessName, "cs2", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            foregroundProcess?.Dispose();
            foregroundProcess = null;
            foregroundProcessId = 0;
            return false;
        }
    }
    
    static void MoveMouse(int deltaX, int deltaY)
    {
        if (IsCs2Foreground())
        {
            mouse_event(MOUSEEVENTF_MOVE, deltaX, deltaY, 0, (int)IntPtr.Zero);
        }
    }
    
    static void DelayExecutionBy(long hns)
    {
        // Convert the delay from 100-nanosecond intervals to a LARGE_INTEGER
        LARGE_INTEGER interval;
        interval.QuadPart = -1 * hns; // Negative value for relative delay

        // Call NtDelayExecution
        NtDelayExecution(false, ref interval);
    }
    
    public class MouseMoveCalculator
    {
        private long lastTime;
        private double remainingMovement;
        private long frequency;

        public MouseMoveCalculator()
        {
            QueryPerformanceCounter(out lastTime);
            QueryPerformanceFrequency(out frequency);
        }

        public void Reset()
        {
            QueryPerformanceCounter(out lastTime);
            remainingMovement = 0;
        }

        public int CalculateMovement(double yawspeed, double sensitivity, double m_yaw)
        {
            long currentTime;
            QueryPerformanceCounter(out currentTime);
            
            long elapsedTime = currentTime - lastTime;
            

            // Calculate movement basedp
            double movement = ((yawspeed / (sensitivity * m_yaw)) * elapsedTime) / frequency;

            // Accumulate remaining movement fractions to ensure precision
            remainingMovement += movement;
            long movementAmount = (long)remainingMovement;

            // Subtract the integer part used, keep the fractional part for next calculation
            remainingMovement -= movementAmount;

            // Update last time for next call
            lastTime = currentTime;

            return (int) movementAmount;
        }
    }
    
    static bool IsKeyDown(int vk)
    {
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }


    static void Main(string[] args)
    {
        
        var configFilePath = "config.json";
        var configFileExists = File.Exists(configFilePath);
        

        string configFile;
        if (!configFileExists)
        {
            var defaultConfig = new Config
            {
                Yaw = new List<int> { 80, 160, 240 },
                Sensitivity = 0.8,
                M_Yaw = 0.022,
                Left = Keys.LBUTTON,
                Right = Keys.RBUTTON,
                Toggle = Keys.XBUTTON1,
                Pause = Keys.F1,
                Inc = Keys.OEM_PLUS,
                Dec = Keys.OEM_MINUS,
                Exit = Keys.F2
            };
            configFile = JsonSerializer.Serialize(defaultConfig, typeof(Config), ConfigJsonContext.Default);
            File.WriteAllText(configFilePath, configFile);
        }
        else
        {
            configFile = File.ReadAllText(configFilePath);
        }
        
        // use system.json
        var config = JsonSerializer.Deserialize(configFile, typeof(Config), ConfigJsonContext.Default) as Config;
        if (config == null)
        {
            throw new Exception("Config file is empty");
        }

        Console.WriteLine(@"
   ____   _____ ______    _____ _    _ _____  ______ 
  / __ \ / ____|  ____|  / ____| |  | |  __ \|  ____|
 | |  | | |    | |__    | (___ | |  | | |__) | |__   
 | |  | | |    |  __|    \___ \| |  | |  _  /|  __|  
 | |__| | |____| |____ _ ____) | |__| | | \ \| |     
  \____/ \_____|______(_)_____/ \____/|_|  \_\_|     ");
        
        Console.WriteLine();
        
        
        Console.WriteLine("Yaw speeds: " + string.Join(", ", config.Yaw));
        Console.WriteLine("Sensitivity: " + config.Sensitivity);
        Console.WriteLine("M_Yaw: " + config.M_Yaw);
        Console.WriteLine("Left: " + config.Left);
        Console.WriteLine("Right: " + config.Right);
        Console.WriteLine("Toggle Yaw: " + config.Toggle);
        Console.WriteLine("Pause: " + config.Pause);
        Console.WriteLine("Increase Yaw: " + config.Inc);
        Console.WriteLine("Decrease Yaw: " + config.Dec);
        Console.WriteLine("Exit: " + config.Exit);
        
        Console.WriteLine();
        Console.WriteLine("To edit bindings, edit config.json and restart the program.");
        Console.WriteLine("Use the keynames from here without the VK_ : https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes");
        Console.WriteLine("IE: VK_LBUTTON is LBUTTON etc, A-Z are the same, 0-9 are the same, F1-F24 are the same, etc.");
        
        
        Console.WriteLine();
        Console.WriteLine("Current yaw speed: " + config.Yaw[0]);
        Console.WriteLine("PAUSED, press " + config.Pause + " to unpause");
        Console.WriteLine("Movement and gameplay hotkeys only work while CS2 is focused. Exit works globally.");
        
        int currentYawIndex = 0;
        bool paused = true;
        var calculator = new MouseMoveCalculator();

        while (true)
        {
            if (IsKeyDown((int) config.Exit))
            {
                Console.WriteLine("Exiting program...");
                break;
            }

            if (!IsCs2Foreground())
            {
                calculator.Reset();
                DelayExecutionBy(3500);
                continue;
            }

            var movement = calculator.CalculateMovement(config.Yaw[currentYawIndex],  config.Sensitivity, config.M_Yaw);
            
            // Check if the left mouse button is pressed and not paused
            if (IsKeyDown((int) config.Left) && !paused)
            {
                // Move the mouse left by the specified speed
                MoveMouse(-movement, 0);
            }

            // Check if the right mouse button is pressed and not paused
            if (IsKeyDown((int) config.Right) && !paused)
            {
                // Move the mouse right by the specified speed
                MoveMouse(movement, 0);
            }

            // Check if the 'P' key is pressed to toggle pause state for left mouse button
            if (IsCs2Foreground() && IsKeyDown((int) config.Pause))
            {
                paused = !paused;
                if (paused)
                {
                    Console.WriteLine("PAUSED press " + config.Pause + " to unpause");
                }
                else
                {
                    Console.WriteLine("ACTIVE press " + config.Pause + " to pause");
                }
                Thread.Sleep(200); // Delay to avoid multiple toggles with one key press
            }

            // Check if the '+' key is pressed to increase yaw speed
            if (IsCs2Foreground() && IsKeyDown((int) config.Inc) && !paused)
            {
                config.Yaw[currentYawIndex] += 10; // Increase yaw speed by 10
                Thread.Sleep(200); // Delay to avoid multiple increments with one key press
                Console.WriteLine($"Yaw speed increased to {config.Yaw[currentYawIndex]}");
            }

            // Check if the '-' key is pressed to decrease yaw speed
            if (IsCs2Foreground() && IsKeyDown((int) config.Dec) && !paused)
            {
                config.Yaw[currentYawIndex] -= 10; // Decrease yaw speed by 10
                Thread.Sleep(200); // Delay to avoid multiple decrements with one key press
                Console.WriteLine($"Yaw speed decreased to {config.Yaw[currentYawIndex]}");
            }
            
            if (IsCs2Foreground() && IsKeyDown((int) config.Toggle) && !paused)
            {
                currentYawIndex = (currentYawIndex + 1) % config.Yaw.Count;
                Console.WriteLine($"Yaw speed toggled to {config.Yaw[currentYawIndex]}");
                Thread.Sleep(200); // Delay to avoid multiple toggles with one key press
            }

            DelayExecutionBy(3500);
        }
    }
}
