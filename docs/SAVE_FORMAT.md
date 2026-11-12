# NARUTO TO BORUTO: SHINOBI STRIKER save format (Steam)

Worked out from two character saves (one fresh, data table version 2710.1000; one heavily
progressed, 2270.1002) and one settings save. Everything below is verified by a byte-exact
round trip of those files unless marked *unconfirmed*.

## Files

Location: `%USERPROFILE%\Saved Games\NARUTO TO BORUTO SHINOBI STRIKER\`

| File | Save class | Contents |
|---|---|---|
| `Customize_v002.sav` | `BP_SaveGame_C` | The character: profile, appearance, loadouts, item box, counters, flags |
| `System_v002.sav` | `BP_SaveSystem_C` | Display and sound settings, licence versions, a 1 MB zero-filled `MasterDataBin` |
| `backup\*.sav` | | Older copies the game keeps |

No encryption, no checksum, no signature. The PS4 version is expected to use the same
layout inside its (encrypted) save container, but that has not been checked.

## Container: UE4 GVAS

Standard Unreal Engine 4.16 `SaveGame` serialisation.

```
"GVAS"
int32  SaveGameVersion        = 2
int32  PackageVersion         = 513
uint16 major, minor, patch    = 4.16.3
uint32 build                  = 0
FString branch                = "++UE4+Release-4.16"
int32  CustomFormatVersion    = 3
int32  count (32), then count x { GUID(16), int32 version }
FString SaveGameClass
property list ... "None"
4 zero bytes
```

`FString` = int32 length, then that many bytes including the terminator. A negative length
means UTF-16 with `-length` characters. Length 0 is a null string.

Each property: `FString name, FString type, int32 size, int32 arrayIndex`, type-specific tag
fields, one `hasGuid` byte, then `size` bytes of payload. A list ends at the name `None`.

UE4 does not write properties that still hold their default value. A fresh save is therefore
missing fields (for example `m_sex`, `m_remakeTicketCount`) that a progressed save has.

Native structs stored as raw bytes instead of a property list: the usual engine ones
(`Vector`, `IntPoint`, ...) plus two of the game's own, both a single little-endian int64:
`NN_Int64` and `NN_ServerCharacterID`.

## Customize_v002.sav

Root: `CustomizeInfo : NN_GameData_ForSave`

| Property | Type | Meaning |
|---|---|---|
| `m_dataTableVersion` | struct | `IDName`, `major`, `minor` of the game data the save was written with |
| `m_playerBaseData` | struct | `m_playerName`, `m_serverCharacterID` (int64), `m_IDCountry`, `m_IDNickname`, `m_IDMaster`, `m_IDShinobiRank`, `m_playerRank`, `m_sex` |
| `m_customInfo`, `m_customInfoSub` | `NN_CustomizeInfo` | Avatar parts by ID name: `IDPartsJacket`, `IDPartsHair`, `IDPartsColorSkin`, `IDVoiceInfo`, `Sex`, ... |
| `m_indexedLobbyOutfit` | struct | The same outfit as data-table row indices (ints), plus `Weapon` and `Available` |
| `m_playerSettings` | array of 16 `NN_GameData_EfficencySetting` | Loadouts, see below |
| `m_compressedDataVersion` | int | 512 |
| `m_compressedDataV2` | byte array | Item box, see below |
| `m_gameCounters` | map name -> int | 260 counters: `ID_Counter_Money`, `..._PlayerExperience`, `..._MasterPoint_<master>`, `..._MasterRank_<master>`, match statistics, `ID_Counter_R1..R128` (unnamed reserve counters used by later updates) |
| `m_gameFlags` | map name -> bool | About 23,000 flags. `ID_Flag_Acquisition_/Open_/Check_<item id>` per item, `ID_Flag_Check_NJT_*` per jutsu, `ID_Reward_Get_*`, mission flags `ID_Flag_*_ML_*`, and about 15,000 unnamed `ID_Flag_R<n>` |
| `m_gameStringDatas` | map name -> string | 21 strings (launch mode, lobby scene lists, reserve) |
| `m_mapScrolls` | map name -> int | Scroll counts by rank (`ID_Scroll_S`, ...) |
| `m_remakeTicketCount` | int | Character remake tickets |
| `m_latestNinjaRank`, `m_latestMatchGrade` | int, name | Last shown rank and match grade |
| `m_loadoutPage`, `m_loadoutSlot`, `m_selectPlayerSettings` | int, int, byte | Selected loadout |
| `RankMatchData`, `EventMatchData` | `NN_GameData_EventMatchData` | `EventID`, `EventIDName`, `EventIndex`, `GradePoint`, `EventMatchNum`, `EventMatchWin` |
| `m_latestMatchScore` | array of 10 structs | `IsWin`, `Kill`, `Killed` |
| `m_offlineAcquiredNinjutsuLog` | array of structs | `UnixEpoch` (int64), `NinjutsuType`, `acquisionMethod`. Only in the newer save |
| `m_reserveInt/Float/FName/MultipleInt` | arrays | Reserve storage. Some slots are in use; meaning *unconfirmed* |
| `m_randomGenerator64Snapshots` | array of 4 structs | Each 624 ints: Mersenne Twister state for reward rolls |

The maps keep every key in a fresh save. The progressed save examined had entries with value
`False` stripped out.

### Loadout (`NN_GameData_EfficencySetting`)

`IDNameRole`, `IDNameWeapon`, `IDNameSubWeapon`, `IDNameJutsu_0..2`, `IDNameKawarimi`
(substitution jutsu), `IDNameSummon`, then five item box slot numbers: `JacketItemIndex`,
`PantsItemIndex`, `AccessoryItemIndex`, `WeaponItemIndex`, `SubWeaponItemIndex`
(-1 = none).

### Item box (`m_compressedDataV2`)

The byte array is several UE4 compressed blocks back to back. Each block:

```
int64 0x9E2A83C1         package tag
int64 0x20000            chunk size
int64 compressedSize
int64 uncompressedSize   (0x20000, less for the last block)
per chunk: int64 compressedSize, int64 uncompressedSize   (one chunk per block here)
zlib data
```

Decompress every block and join the results to get one stream:

```
int32 streamLength - 4
int32 slotCount          = 4020
slotCount x {
    FString id           "ID_CustomJacket_M_16", "ID_Weapon_Kunai", "None" for an empty slot
    int32   index        the slot's own number, 0..4019
    int32   equipCount   how many of the 16 loadouts reference this slot
    FString type         "ENN_ItemType::ENN_ItemType_<Weapon|Jacket|Pants|Accessory|Facepaint|NinjaWeapon|Summon|None>"
    int32   unknown1     always 0 in both saves
    int32   unknown2     0 in all but one slot
    FString skill1
    FString skill2
    FString skill3       "ID_Skill_..." or "None"
    byte    flag         unconfirmed; set on most items that carry skills
}
```

`equipCount` is confirmed: recounting loadout references reproduces every value in the fresh
save exactly.

## What is not known

- Whether the server keeps its own copy of rank, currency or unlocks and overrides the file.
  `m_serverCharacterID` suggests some state is tied to a server record.
- The meaning of `flag`, `unknown1`, `unknown2` in item slots, of the `ID_Counter_R*` and
  `ID_Flag_R*` entries, and of the reserve arrays.
- Whether paid DLC content is gated by anything in the save. The editor does not touch
  store entitlements.
