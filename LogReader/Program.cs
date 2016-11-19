using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LogReader
{
	// Represents a single log entry consisting of the time at which the
	// measurements were taken and a list of values representing the
	// measured temperatures. 
	class LogEntry
	{
		public double[] Sensors { get; } = new double[4];
		public DateTime LogTime { get; }

		public LogEntry(DateTime logTime)
		{
			LogTime = logTime;
		}

		public override string ToString()
		{
			var sensors = Sensors.Select((s, i) => $"S{i + 1}: {s:F1}°C");
			var sensorString = string.Join("   ", sensors);

			return $"{LogTime:yyyy-MM-dd hh:mm:ss} -- {sensorString}";
		}
	}

	internal class ParseException : Exception
	{
		public ParseException(string message) : base(message) { }
	}

	internal class Program
	{
		// The length, in bytes, of a single data frame
		private const int FrameLength = 16;
		// The number of sensors on the device
		private const int SensorCount = 4;
		// Serial port polling interval, in milliseconds
		private const int ProbeDelay = 10;
		// The name of the CSV file to which log entries should be written.
		private const string CsvFileName = "temperature-log.csv";

		//private List<LogEntry> logEntries = new List<LogEntry>();

		internal void StartLogging()
		{
			// Temperature readings are accumulated into a LogEntry object
			// until a reading for every sensor has been acquired.
			// As the first entry may not be fully populated, it is discarded.
			var accumulator = new LogEntry(DateTime.MinValue);
			var firstEntry = true;
			var port = GetPort();
			while (port.IsOpen)
			{
				// port.Read does not block and wait for the buffer to be filled.
				// so if not enough data is available, the buffer may only be partially 
				// full. For the sake of convencience, we want the buffer to represent,
				// exactly 1 data frame from beginning to end, because it simplifies
				// parsing. Therefore, we have to wait until enough data is available 
				// to fill the buffer.
				if (port.BytesToRead < FrameLength)
				{
					Thread.Sleep(10);
					continue;
				}

				var buf = new byte[FrameLength];
				port.Read(buf, 0, FrameLength);
				var str = Encoding.ASCII.GetString(buf);

				bool accumulatorIsFull;
				try
				{
					accumulatorIsFull = ParseFrame(str, accumulator);
				}
				catch (ArgumentException)
				{
					Console.Error.WriteLine("Skipping misaligned frame");
					// Wait a moment to ensure we have some data in the buffer.
					Thread.Sleep(ProbeDelay * 10);
					// Read a single byte to offset our alignment.
					port.Read(new byte[1], 0, 1);
					continue;
				}
				catch (ParseException e)
				{
					Console.Error.WriteLine(e.Message);
					continue;
				}
				if (accumulatorIsFull)
				{
					// Discard the first entry, save every entry after that.
					if (firstEntry)
					{
						firstEntry = false;
					}
					else
					{
						Console.WriteLine(accumulator);
						WriteToCsv(accumulator, CsvFileName);
						//logEntries.Add(accumulator);
					}
					accumulator = new LogEntry(DateTime.Now);
					Thread.Sleep(ProbeDelay);
				}
			}
		}

		private static void WriteToCsv(LogEntry entry, string filename)
		{
			var created = !File.Exists(filename);
			using (var writer = new StreamWriter(File.OpenWrite(filename), Encoding.UTF8))
			{
				if (created)
				{
					// Add a CSV header if the file is newly created.
					var headers = string.Join(",", entry.Sensors.Select((val, i) => $"Sensor{i + 1}"));
					writer.WriteLine("Date," + headers);
				}
				var values = string.Join(",", entry.Sensors.Select(s => $"\"{s}\""));
				writer.WriteLine($"\"{entry.LogTime:R}\",{values}");
			}
		}


		private static bool ParseFrame(string frame, LogEntry entry)
		{
			// We can use a regex lookup to ensure our input is correctly formatted.
			var rgx = new Regex(@"4(?<display>[1234])0(?<unit>[12])(?<sign>[01])(?<decimal>[0123])(?<reading>[0-9]{8})");
			var match = rgx.Match(frame);

			if (!match.Success)
			{
				throw new ArgumentException("Invalid frame data.");
			}
			return ReadFrame(match, entry);
		}

		private static bool ReadFrame(Match match, LogEntry logAccumulator)
		{
			var displayStr = match.Groups["display"].Value;       // sensor/display number
			var unit = match.Groups["unit"].Value;                // unit (°C/°F)
			var sign = match.Groups["sign"].Value;                // Temperature sign (positive/negative)
			var decimalPosStr = match.Groups["decimal"].Value;    // Decimal mark position
			var reading = match.Groups["reading"].Value;          // Temperature reading

			var displayIndex = int.Parse(displayStr) - 1;
			var decimalPos = int.Parse(decimalPosStr);
			var value = int.Parse(reading);

			// Divide the temperature reading by 10^decimalPos
			// to put the decimal mark at the right place.
			var temperature = value / Math.Pow(10, decimalPos);

			if (sign == "1")
			{
				temperature *= -1;
			}
			else if (sign != "0")
			{
				throw new ParseException($"Invalid sign byte. Expected 0 or 1 but got '{sign}'");
			}
			// Now store the value.
			logAccumulator.Sensors[displayIndex] = temperature;
			return displayIndex + 1 == SensorCount;
		}

		// Grab the first discovered serial port and open it.
		private static SerialPort GetPort()
		{
			var portName = SerialPort.GetPortNames().First();
			var port = new SerialPort(portName);
			port.Open();
			return port;
		}

		private static void Main(string[] args)
		{
			new Program().StartLogging();
		}
	}
}

