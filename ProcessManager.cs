﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;

namespace EAS_Decoder {
	static class ProcessManager {
		static string _soxDirectory = null;
		public static string SoxDirectory {
			get {
				return _soxDirectory;
			}
			set {
				if (_soxDirectory == null) {
					_soxDirectory = value;
				}
			}
		}
		static string _ffmpegDirectory = null;
		public static string FfmpegDirectory {
			get {
				return _ffmpegDirectory;
			}
			set {
				if (_ffmpegDirectory == null) {
					_ffmpegDirectory = value;
				}
			}
		}

		static int FailedToLoad(StreamReader stderr) {
			bool MADFailedToLoad = false;
			bool fileFailedToOpen = false;

			string line = stderr.ReadLine();
			while (line != null) {
				if (line.Contains("Unable to load MAD decoder library")) {
					MADFailedToLoad = true;
				}
				if (line.Contains("can't open input file")) {
					fileFailedToOpen = true;
				}
				line = stderr.ReadLine();
			}

			return MADFailedToLoad ? -1 : fileFailedToOpen ? -2 : 0;
		}

		static ProcessStartInfo GetStartInfo(string args, bool redirectStdOut, bool redirectStdErr) {
			return new ProcessStartInfo {
				FileName = "cmd",
				Arguments = $"/C \"{args}\"",
				WindowStyle = ProcessWindowStyle.Hidden,
				UseShellExecute = false,
				CreateNoWindow = false,
				WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
				RedirectStandardOutput = redirectStdOut,
				RedirectStandardError = redirectStdErr
			};
		}

		public static void ConvertRAWToMP3(string src, string dest) {
			ProcessStartInfo startInfo = GetStartInfo($"sox -r 22050 -e signed -b 16 -t raw \"{src}\" -t mp3 \"{dest}\"", false, true);
			using (Process soxProcess = Process.Start(startInfo)) {
				string line = soxProcess.StandardError.ReadLine();
				while (line != null) {
					Console.WriteLine(line);
				}
				soxProcess.WaitForExit();
			}
		}

		static void ConvertRAWToMP3(string filename) {
			ConvertRAWToMP3($"{filename}.raw", $"{filename}.mp3");
		}

		static string AddLeadingZero(int value) {
			return $"{(value < 10 ? "0" : "")}{value}";
		}

		static void SaveEASRecording(ref FileStream easRecord, FixedSizeQueue<byte> bufferBefore, string fileName) {
			while (!bufferBefore.IsEmpty) {
				byte[] b = new byte[1];
				if (bufferBefore.TryDequeue(out b[0])) {
					easRecord.Write(b);
				}
			}

			easRecord.Close();
			easRecord = null;

			ConvertRAWToMP3(fileName);
			Console.WriteLine($"Alert saved to {fileName}.mp3\n");
			File.Delete($"{fileName}.raw");
		}

		static string Commas(ulong value) {
			return string.Format("{0:#,###0}", value);
		}

		public static uint bitrate;
		static DateTime fileCreated = DateTime.Now;
		static FixedSizeQueue<byte> bufferBefore = null;
		static FileStream easRecord = null;
		static readonly string[] months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
		public static int ConvertAndDecode(string inputFile, string inputFileType, string outputFile) {
			ProcessStartInfo startInfo = GetStartInfo($"ffmpeg -hide_banner -loglevel warning -i \"{inputFile}\" -f {inputFileType} - | sox -V2 -V2 -t {inputFileType} - -t raw -e signed-integer -b 16 -r 22050 - remix 1", true, true);
			FileStream fs = null;
			if (outputFile != null) {
				File.WriteAllText(outputFile, string.Empty);
				fs = new FileStream(outputFile, FileMode.OpenOrCreate);
			}

			ulong bytesReadIn = 0;
			using (Process soxProcess = Process.Start(startInfo)) {
				if (!Program.SuppressInfo) {
					Console.WriteLine("info: ffmpeg and sox have started");
				}
				soxProcess.EnableRaisingEvents = true;
				soxProcess.BeginErrorReadLine();
				soxProcess.ErrorDataReceived += new DataReceivedEventHandler((s, e) => {
					if (!string.IsNullOrWhiteSpace(e.Data)) {
						Console.WriteLine($"proc: {e.Data}\n");	// arguments have been modified in ffmpeg so that it only prints to standard error when warnings are raised or errors occur
					}
				});

				FileStream baseStream = (FileStream) soxProcess.StandardOutput.BaseStream;
				int lastRead = 0;
				bool record = false;
				bool lastRecord = false;
				string fileName = null;
				bool needToBuffer = true;

				Console.WriteLine();
				do {    // sox nor ffmpeg will not produce anything on stdout if an error occurs
					byte[] buffer = new byte[8192];
					lastRead = baseStream.Read(buffer, 0, buffer.Length);
					bytesReadIn += (ulong) lastRead;
					Tuple<bool, uint, uint> info = null;
					if (lastRead > 0) {
						info = Decode.DecodeEAS(buffer, lastRead);
						lastRecord = record;
						record = info.Item1;
						if (fs != null) {
							fs.Write(buffer, 0, lastRead);
							fs.Flush();
						}
					}
					if (!record && !lastRecord) {
						Console.CursorTop -= 1;
						Console.WriteLine($"Total bytes read in: {Commas(bytesReadIn)}");
					} else if (!record && lastRecord) {
						Console.WriteLine();
					}

					if (bufferBefore != null) {
						if (needToBuffer) {
							for (int i = 0; i < lastRead; i++) {
								bufferBefore.Enqueue(buffer[i]);
							}
							if (easRecord != null && bufferBefore.Size == bufferBefore.Count) {
								Console.WriteLine("Stopped recording EAS alert");
								SaveEASRecording(ref easRecord, bufferBefore, fileName);
							}
						}
						if (record && easRecord == null) {
							Console.WriteLine("Recording EAS alert");
							DateTime recordingStarted = DateTime.Now;
							string month = $"{AddLeadingZero(recordingStarted.Month)}{months[recordingStarted.Month - 1]}";
							string time = $"{AddLeadingZero(recordingStarted.Hour)}{AddLeadingZero(recordingStarted.Minute)}{AddLeadingZero(recordingStarted.Second)}";
							fileName = $"{recordingStarted.Year}_{month}_{recordingStarted.Day} - {time}";

							int idx = 1;
							while (File.Exists($"{fileName} ({idx}).raw")) {
								idx++;
							}
							fileName += $" ({idx})";

							easRecord = new FileStream($"{fileName}.raw", FileMode.OpenOrCreate);
							while (!bufferBefore.IsEmpty) {
								byte[] b = new byte[1];
								if (bufferBefore.TryDequeue(out b[0])) {
									easRecord.Write(b);
								}
							}
							needToBuffer = false;
						} else if (record && easRecord != null) {
							if (needToBuffer) {
								while (!bufferBefore.IsEmpty) {
									byte[] b = new byte[1];
									if (bufferBefore.TryDequeue(out b[0])) {
										easRecord.Write(b);
									}
									needToBuffer = false;
								}
							} else {
								easRecord.Write(buffer, 0, lastRead);
							}
						} else if (!record && easRecord != null && !needToBuffer) {
							easRecord.Write(buffer, 0, lastRead);
							needToBuffer = true;
							bufferBefore = new FixedSizeQueue<byte>(bitrate * 5);
						}
					}
				} while (lastRead > 0);

				if (easRecord != null) {
					SaveEASRecording(ref easRecord, bufferBefore, fileName);
				}
				soxProcess.WaitForExit();
			}
			return 0;
		}

		public static int GetFileInformation(string filepath, bool record, uint duration) {
			fileCreated = DateTime.Now;
			ProcessStartInfo startInfo = GetStartInfo($"sox --i \"{filepath}", true, true);

			int didNotLoad;
			using (Process soxProcess = Process.Start(startInfo)) {
				while (!soxProcess.StandardOutput.EndOfStream) {
					string[] line = soxProcess.StandardOutput.ReadLine().Split(' ');
					if (line[0] == "Bit" && line[1] == "Rate") {
						string number = line[^1][0..^1];
						char multiplier = line[^1][^1];
						if (decimal.TryParse(number, out decimal br)) {
							switch (multiplier) {
								case 'k':
									br *= 1000M;
									break;
								case 'M':
									br *= 1000000M;
									br /= 8;
									break;
								default:
									Console.WriteLine($"Unknown multiplier {multiplier}. Program may not function correctly.");
									break;
							}
							bitrate = decimal.ToUInt32(br);
						}
					}
				}

				didNotLoad = FailedToLoad(soxProcess.StandardError);
				soxProcess.WaitForExit();
			}

			if (didNotLoad != 0) {
				return didNotLoad;
			}

			if (Program.Livestream) {
				bitrate = 22050 * 3;
			}

			if (record) {
				bufferBefore = new FixedSizeQueue<byte>(bitrate * duration);
			}
			return 0;
		}
	}
}
