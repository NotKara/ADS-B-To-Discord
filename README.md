


#  ADS-B Discord Bot

A Discord bot written in **.NET 9 / Discord.Net** that connects to your local **ADS-B receiver (dump1090 / PiAware)** and posts structured aircraft statistics into a Discord channel.

The bot groups aircraft by type, shows callsigns and registrations, and provides a daily summary of all traffic seen by your receiver.

---

##  Features

-  **Live ADS-B polling** — reads directly from your dump1090 `aircraft.json`.
-  **Discord embeds** — aircraft grouped by type (e.g. A359, B789, B738).
-  **Sticky callsigns** — once a callsign is seen, it will not revert to hex-only when the aircraft goes out of range.
-  **Daily stats** — resets at 00:00 UTC with a fresh summary each day.
-  **Handles Discord embed limits** — splits into multiple messages automatically if there are more than 25 aircraft types.
-  **Most popular type so far** — shows the most common type of the day at the top of the embed.
-  **Fallback registry support** — add your own overrides for missing/incorrect aircraft in `aircraft-registry-fallback.csv`.
-  **Console logging** — logs `[NEW]`, `[UPDATE]`, `[IGNORE]`, and `[FALLBACK]` events for debugging.

---

##  Data Sources

- **Main registry**  
  `aircraft-database-complete-YYYY-MM.csv`  
  A global ICAO24 → registration/type database.  
  Place this file in a folder such as `/home/pi/data/` (or any path you prefer — just update DailyAircraftStatsService.cs to match).


- **Fallback registry (optional)**  
  `aircraft-registry-fallback.csv`  
  Your personal overrides for missing or incorrect entries.  
  Example:

  ```csv
  hex,registration,type,operator
  8966c5,A6-BNJ,B789,Etihad Airways
  750523,9M-XBG,A333,AirAsia X
  7cad4a,VH-8IK,B38M,Virgin Australia

You can get a local database file here https://opensky-network.org/datasets/#metadata 


## PLEASE NOTE THIS DOES NOT INCLUDE HOW TO SETUP A DISCORD BOT YOU WILL NEED TO DO THIS YOURSELF. THIS IS A DROP IN SCRIPT 
In your bot file please add the following 

private DailyAircraftStatsService? _aircraftStatsService;

 if (_aircraftStatsService == null)
        {
            _aircraftStatsService = new DailyAircraftStatsService(_client);
            _aircraftStatsService.Start();
            Console.WriteLine("[INFO] DailyAircraftStatsService started.");
        }




  ## Notes

- This will only work with **.NET-based Discord bots**.  
  You can find the Discord.Net docs here: https://github.com/discord-net/Discord.Net  

- UTC time will only be correct if you have **NTP servers configured at the hardware level** on your Pi.  
  Without proper NTP, daily resets may drift or occur at the wrong time.  

- If you are located in **Australia**, I highly recommend reaching out to the **Department of Industry, Science and Resources** to get whitelisted for access to the National Measurement Institute’s time servers.  
  More info here: https://www.industry.gov.au/national-measurement-institute/nmi-services/physical-measurement-services/time-and-frequency-services
