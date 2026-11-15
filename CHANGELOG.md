# Changelog

## 2.0.0

This release adds multi-radio operation, other radio families and the
operational safeguards that were missing; the signalling of the basic profile
is unchanged. An existing `config.json` is converted automatically: the two
radios keep their addresses and appear as LOCALIZER and GLIDEPATH, and the
original file is kept as `config.v1-backup.json`.

### New
- **1 to 8 radios.** Each radio has its own name, addresses, SIP users and
  options, entered in Settings → Radios. Radios can be added, removed and
  re-ordered.
- **Grid view** with square tiles, four per row; radios beyond the fourth wrap
  to the next row. The classic view with tall cards remains (two per column).
  One button switches between them.
- **Radio profiles:** basic (tested on Jotron radios), generic ED-137B and
  generic ED-137C (`WG67-Version`, standard `Subject`/`Priority`, R2S payload
  type and `type`/`txrxmode`/`fid` attributes in the offer, PTT-ID assigned by
  the radio). The generic profiles are **not verified on hardware**.
- **Link modes:** separate receiver and transmitter, transceiver (one session)
  and receive-only.
- **Automatic reconnect** with back-off (2, 4, 8, 15 s) and optional
  **connect on startup**. "Connect all / disconnect all" button.
- **Status line on every radio** with the reason for a failure
  ("no answer within 8 s", "the radio answered 486 Busy Here", …). A running or
  half-finished connection can be cancelled or disconnected at any time.
- **Transmit time limit** (default 180 s, 0 = off) against a stuck PTT, with a
  banner on the main window.
- **Per-radio KEY switch:** the global PTT key keys only the selected radios.
  On-screen PTT buttons key their own radio.
- **Audio:** input and output device selection, per-radio volume (0–200 %) and
  left/right balance, MON switch per radio.
- **Microphone clean-up:** capture at 48 kHz with a proper conversion to the
  8 kHz of the radio link, removal of rumble and hum below the voice band, a
  soft limiter, and an optional **background noise reduction** (normal about
  12 dB, strong about 18 dB).
- **Adjustable transmit timing:** delay before speech (default 200 ms) and
  hold after speech (default 150 ms).
- **Indicators from the radio:** squelch (SQ) and "on air" confirmation (PTT
  echo), read from the ED-137 RTP header extension.
- **Log window** and daily log files (30 days), crash log, global exception
  handling. A fault in the user interface no longer takes the calls down.
- **Settings PIN** is optional and stored as a salted PBKDF2 hash. The former
  fixed PIN is gone.
- **Frequency:** VHF 108–156 MHz and UHF 225–400 MHz, value re-read every 20 s
  while connected. Radios without frequency control show a free-text frequency
  label.
- **SNMP per radio:** community, port, version (v2c or v1), the frequency
  object (OID), a separate object for the transmitter, the number format
  (kHz, Hz or text in MHz) and separate SNMP addresses for receiver and
  transmitter. **Read now** tries the values without saving them.
- **Languages:** the user interface is available in 34 languages (European
  languages, Turkish, Russian, Ukrainian, Arabic, Persian, Urdu, Kazakh, Uzbek,
  Kyrgyz, Turkmen, Tajik, Azerbaijani, Japanese, Chinese), chosen in Settings →
  General or taken from Windows, switched without a restart. Arabic, Persian
  and Urdu use a right-to-left layout. The translations were made by machine
  and have not been reviewed; English is the reference.
- Options: hide the PTT key from other applications, always on top,
  organisation text and optional logo in the footer.
- `--demo` mode with simulated radios.
- Illustrated first setup guide in English and Turkish (`docs/GUIDE.md`,
  `docs/GUIDE.tr.md`).

### Changed
- The placeholder frequency `123.000` is no longer shown as if it were read
  from the radio. An unknown frequency is displayed as `---.---`, a stored but
  unconfirmed value is dimmed.
- Receiver and transmitter are called in parallel; an unanswered call fails
  after 8 s instead of the 32 s SIP timeout.
- Speech captured while the transmitter keys up is delayed instead of
  discarded. After the release, the speech still inside the sound device and
  behind that delay is sent and the transmitter is held keyed a little longer,
  so the first syllable and the last word are not cut.
- The noise gate no longer cuts the beginning of a word: the frames leading
  up to it are sent when the gate opens, and the gate fades instead of
  switching hard.
- Received audio is played from a small reserve (no drop-outs in the first
  word of a reception), and clock drift no longer throws away 0.4 s of audio
  at a time.
- The default PTT key for new installations is F9 (it was the space bar).
- One microphone capture is shared by all radios and restarts itself when the
  device stalls or is re-plugged.
- The window sizes itself to its content and scales down on small screens.
- Only one instance can run; starting it again brings the running one forward.
- Saving the settings re-establishes only the calls whose address, user or
  profile changed.

### Fixed
- SDP attributes of the offer (`R2S-KeepAlivePeriod`, `R2S-KeepAliveMultiplier`,
  `ptime`) were written without the `a=` prefix. The radios used so far
  ignored the malformed lines; a stricter radio would reject the call.
- The local address announced in SDP was not taken from the route to the radio
  (the port was parsed as part of the host name), which picked the wrong
  network adapter on PCs with more than one.
- A call URI without a user part produced an invalid request URI.

### Security
- Audio, keepalives and RTCP are accepted only from the media address the
  radio signalled. Before, a packet from any address on the network was
  played, could switch the squelch and "on air" indicators, and, when it
  arrived before the radio's first packet, drew the transmit audio and the
  keepalives to its sender.
- SIP messages are accepted only from the addresses of the called radio.
  Before, a BYE from any address that quoted the Call-ID ended the call.
- An SNMP answer is accepted only from the unit that was asked.
- Dropped packets are logged; a flood of them produces only a few lines.

### Removed
- Unused code, the second installer script and the embedded fixed PIN.
- The obfuscation step of the installer build.

### Verification
- The regression suite (75 checks) covers PTT arbitration for up to eight
  radios, the transmit time limit, configuration migration, the settings logic,
  the audio processing with test signals,
  the SIP/RTP engine against a simulated radio on 127.0.0.1 for the basic
  and ED-137B/C profiles (INVITE contents, keepalives, PTT bits, speech tail,
  hang-up, refusal, timeout), frequency control against a simulated SNMP agent
  on 127.0.0.1, the completeness of the language files, and forged or
  malformed packets sent from a second loopback address.
- Tested on Jotron radios (basic profile): connect, receive, transmit with
  PTT, frequency change, start and end of a transmission not cut, long idle
  periods, automatic reconnect and the sound of the audio processing.
- **Not verified:** the generic ED-137B/C profiles and the SNMP options other
  than the default of the basic profile on any hardware, and the translations
  by native speakers. Run an acceptance test on your own radios before
  operational use.

## 1.0.2
- Valid incoming R2S keepalives refresh the RTP inactivity watchdog; idle calls
  no longer drop after about 30 seconds.
- Playback closes before capture shutdown; late audio callbacks cannot revive a
  closed sink.
- Old queued hang-up notifications no longer clear a reconnected channel.
