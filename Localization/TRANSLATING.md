# Translating RadioDeck

Every language is one file in this folder: `<code>.json`, a flat JSON object
`"key": "text"`. `en.json` is the reference. A key that is missing in another
file is shown in English.

## Rules

- Same keys, same order as `en.json`. Translate the values only.
- UTF-8 without BOM, valid JSON. Keep `\n` and `\"` escapes as they are.
- Keep the placeholders `{0}`, `{1}` exactly; they may be moved inside the
  sentence.
- Do not translate: `RadioDeck`, `PTT`, `RX`, `TX`, `SIP`, `SNMP`, `OID`,
  `WG67-Version`, `ED-137`, `ED-137B`, `ED-137C`, `MHz`, `kHz`, `Hz`, `dB`,
  `PIN`, `IP`, `config.json`, `KEY` (the name of a switch on the main window),
  `Esc`, `Windows`. Numbers stay in Western digits (0-9).
- Texts that start with a lower-case letter (`Status_NoAnswer`,
  `Status_RadioAnswered`, `Status_CallFailed`, `Status_Retrying`, `Freq_Err…`,
  `Snmp_NoAnswer`, `Snmp_NoSuchObject`, `Snmp_NotANumber`) are fragments shown
  after a colon or a dash, e.g. `RX 192.0.2.11:5060: no answer within 8 s`.
  Keep them as fragments without a full stop.
- Length limits (the text sits on a small button or tile):
  - `Card_Connect`, `Card_Disconnect`, `Card_Cancel`: at most 12 characters
  - `Card_Set`: at most 8 characters
  - `Card_Transmitting`: at most 14 characters, in capitals if the script has them
  - `Set_Add`, `Set_Remove`, `Set_Up`, `Set_Down`, `Btn_…`: at most 12 characters
  - `Set_Tab…`: at most 12 characters
  - `Status_Disconnected`, `Status_Connecting`, `Status_Connected`,
    `Status_NotConnected`, `Status_RxOnly`, `Status_TxOnly`: at most 22 characters
- Style: short, neutral, formal; the wording of professional software and of
  aeronautical radio operation in that language (receiver, transmitter,
  transceiver, squelch, push-to-talk, frequency, to key a transmitter).

## Meaning of some terms

- *keyed*: the transmitter is switched to transmit by PTT.
- *Capture* (`Ptt_Capture`): the program waits for a key press and takes that
  key as the PTT key.
- *KEY switch*: a switch on each radio that decides whether the PTT key
  transmits on that radio.
- *Follow new lines* (`Log_Follow`): the log scrolls to the newest line.
- *community* (SNMP): the SNMP community string; keep the word "community" if
  the language has no established term.
- *Profile default*: the value that belongs to the selected radio type.
- *squelch*: use the term radio operators use in that language; keep
  "squelch" where it is the usual word.
- *Balance*: left/right stereo balance of the headset.
