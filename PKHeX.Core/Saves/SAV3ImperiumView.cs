using System;
using System.Buffers.Binary;

namespace PKHeX.Core;

/// <summary>
/// Read-only compatibility view for Pokémon Emerald Imperium v1.3.1.
/// Imperium Pokémon are converted to temporary PK9 objects for display in PKHeX.
/// The original save buffer is never modified or exported by this class.
/// </summary>
public sealed class SAV3ImperiumView : SaveFile
{
    private const int SectorSize = 0x1000;
    private const int SectorUsed = 0xFF4;
    private const int LogicalSectorCount = 28;
    private const uint SectorSignature = 0x08012025;

    private const int PartyCountOffset = 0x234;
    private const int PartyOffset = 0x238;
    private const int ImperiumPartySize = 100;
    private const int ImperiumStoredSize = 80;
    private const int StorageFirstSlotOffset = 4;

    private readonly byte[] PartyDataBuffer = new byte[6 * PokeCrypto.SIZE_8PARTY];
    private readonly byte[] BoxDataBuffer = new byte[14 * 30 * PokeCrypto.SIZE_8PARTY];

    private int PartyCountValue;
    private bool ChecksumState = true;

    protected internal override string ShortSummary => "Emerald Imperium v1.3.1 (read-only)";
    public override string Extension => ".sav";

    public SAV3ImperiumView(Memory<byte> data) : base(data, exportable: false)
    {
        Party = 0;
        Box = 0;
        Language = (int)LanguageID.English;
        OT = "Imperium";
        Initialize(data.Span[..0x20000]);
    }

    protected override SAV3ImperiumView CloneInternal() => new(Buffer.ToArray());

    public static bool IsImperium131(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x20000)
            return false;

        Span<bool> found = stackalloc bool[LogicalSectorCount];
        int foundCount = 0;

        for (int offset = 0; offset < 0x20000; offset += SectorSize)
        {
            var sector = data.Slice(offset, SectorSize);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(sector[0xFF4..]);
            if (id >= LogicalSectorCount)
                continue;

            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(sector[0xFF8..]);
            if (signature != SectorSignature)
                return false;

            if (!found[id])
            {
                found[id] = true;
                foundCount++;
            }
        }

        return foundCount == LogicalSectorCount;
    }

    private void Initialize(ReadOnlySpan<byte> raw)
    {
        var logical = new byte[LogicalSectorCount][];
        for (int offset = 0; offset < 0x20000; offset += SectorSize)
        {
            var sector = raw.Slice(offset, SectorSize);
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(sector[0xFF4..]);
            if (id >= LogicalSectorCount)
                continue;

            logical[id] = sector[..SectorUsed].ToArray();

            ushort expected = Checksums.CheckSum32(sector[..SectorUsed]);
            ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(sector[0xFF6..]);
            if (expected != actual)
                ChecksumState = false;
        }

        var large = new byte[16 * SectorUsed];
        for (int i = 0; i < 16; i++)
            logical[i + 1].CopyTo(large, i * SectorUsed);

        var storage = new byte[11 * SectorUsed];
        for (int i = 0; i < 11; i++)
            logical[i + 17].CopyTo(storage, i * SectorUsed);

        PartyCountValue = Math.Min(large[PartyCountOffset], (byte)6);
        for (int i = 0; i < PartyCountValue; i++)
        {
            var src = large.AsSpan(PartyOffset + (i * ImperiumPartySize), ImperiumPartySize);
            var pk = ConvertImperiumMon(src, isParty: true);
            if (pk is null)
                continue;
            pk.WriteEncryptedDataParty(PartyDataBuffer.AsSpan(i * PokeCrypto.SIZE_8PARTY, PokeCrypto.SIZE_8PARTY));
        }

        for (int i = 0; i < 14 * 30; i++)
        {
            int offset = StorageFirstSlotOffset + (i * ImperiumStoredSize);
            if (offset + ImperiumStoredSize > storage.Length)
                break;

            var src = storage.AsSpan(offset, ImperiumStoredSize);
            if (!IsImperiumPokemonPresent(src))
                continue;

            var pk = ConvertImperiumMon(src, isParty: false);
            if (pk is null)
                continue;
            pk.WriteEncryptedDataParty(BoxDataBuffer.AsSpan(i * PokeCrypto.SIZE_8PARTY, PokeCrypto.SIZE_8PARTY));
        }
    }

    private static bool IsImperiumPokemonPresent(ReadOnlySpan<byte> data)
        => data.Length >= ImperiumStoredSize && data[..ImperiumStoredSize].ContainsAnyExcept<byte>(0, 0xFF);

    private static PK9? ConvertImperiumMon(ReadOnlySpan<byte> source, bool isParty)
    {
        if (source.Length < ImperiumStoredSize)
            return null;

        byte[] decrypted = new byte[isParty ? ImperiumPartySize : ImperiumStoredSize];
        source[..decrypted.Length].CopyTo(decrypted);
        PokeCrypto.Decrypt3(decrypted);

        var d = decrypted.AsSpan();
        ushort imperiumSpecies = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(d[0x20..]) & 0x07FF);
        ushort species = GetNationalSpecies(imperiumSpecies);
        if (species == 0 || species > Legal.MaxSpeciesID_9_T2)
            return null;

        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(d);
        uint id32 = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        int language = d[0x12] & 0x07;
        if (language is <= 0 or > (int)LanguageID.ChineseT)
            language = (int)LanguageID.English;

        var pk = new PK9
        {
            Species = species,
            PID = pid,
            EncryptionConstant = pid,
            ID32 = id32,
            Language = language,
            Version = GameVersion.SL,
            EXP = BinaryPrimitives.ReadUInt32LittleEndian(d[0x24..]) & 0x001F_FFFF,
            OriginalTrainerFriendship = d[0x29],
            EV_HP = d[0x38],
            EV_ATK = d[0x39],
            EV_DEF = d[0x3A],
            EV_SPE = d[0x3B],
            EV_SPA = d[0x3C],
            EV_SPD = d[0x3D],
            PokerusStrain = d[0x44] >> 4,
            PokerusDays = d[0x44] & 0x0F,
        };

        pk.Nickname = StringConverter3.GetString(d.Slice(0x08, 10), language);
        pk.OriginalTrainerName = StringConverter3.GetString(d.Slice(0x14, 7), language);

        ushort item = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(d[0x22..]) & 0x03FF);
        if (item <= Legal.MaxItemID_9_T2)
            pk.HeldItem = item;

        pk.Move1 = ReadImperiumMove(d, 0x2C);
        pk.Move2 = ReadImperiumMove(d, 0x2E);
        pk.Move3 = ReadImperiumMove(d, 0x30);
        pk.Move4 = ReadImperiumMove(d, 0x32);
        pk.Move1_PP = d[0x34] & 0x7F;
        pk.Move2_PP = d[0x35] & 0x7F;
        pk.Move3_PP = d[0x36] & 0x7F;
        pk.Move4_PP = d[0x37] & 0x7F;

        uint iv32 = BinaryPrimitives.ReadUInt32LittleEndian(d[0x48..]);
        pk.IV_HP = (int)(iv32 & 0x1F);
        pk.IV_ATK = (int)((iv32 >> 5) & 0x1F);
        pk.IV_DEF = (int)((iv32 >> 10) & 0x1F);
        pk.IV_SPE = (int)((iv32 >> 15) & 0x1F);
        pk.IV_SPA = (int)((iv32 >> 20) & 0x1F);
        pk.IV_SPD = (int)((iv32 >> 25) & 0x1F);
        pk.IsEgg = ((iv32 >> 30) & 1) != 0;

        pk.Nature = (Nature)(pid % 25);
        pk.StatAlignment = pk.Nature;
        pk.Gender = EntityGender.GetFromPID(species, pid);

        int ability = pk.PersonalInfo.Ability1;
        pk.Ability = ability;
        pk.AbilityNumber = 1;

        pk.RefreshChecksum();
        return pk;
    }

    private static ushort ReadImperiumMove(ReadOnlySpan<byte> data, int offset)
    {
        ushort move = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) & 0x07FF);
        return move <= Legal.MaxMoveID_9_T2 ? move : (ushort)0;
    }

    private static ushort GetNationalSpecies(ushort imperiumSpecies) => imperiumSpecies switch
    {
        1292 => 909, // Fuecoco in Imperium v1.3.1
        <= 1025 => imperiumSpecies,
        _ => 0,
    };

    public override bool ChecksumsValid => ChecksumState;
    public override string ChecksumInfo => ChecksumState ? "Imperium sector checksums are valid." : "One or more Imperium sector checksums are invalid.";
    protected override void SetChecksums() { }

    public override IPersonalTable Personal => PersonalTable.SV;
    public override ReadOnlySpan<ushort> HeldItems => Legal.HeldItems_SV;

    public override ushort MaxMoveID => Legal.MaxMoveID_9_T2;
    public override ushort MaxSpeciesID => Legal.MaxSpeciesID_9_T2;
    public override int MaxAbilityID => Legal.MaxAbilityID_9_T2;
    public override int MaxItemID => Legal.MaxItemID_9_T2;
    public override int MaxBallID => Legal.MaxBallID_9;
    public override GameVersion MaxGameID => Legal.MaxGameID_HOME;

    public override int BoxCount => 14;
    public override int MaxEV => EffortValues.Max252;
    public override byte Generation => 9;
    public override EntityContext Context => EntityContext.Gen9;
    public override int MaxStringLengthTrainer => 12;
    public override int MaxStringLengthNickname => 12;

    public override int SIZE_STORED => PokeCrypto.SIZE_8PARTY;
    public override int SIZE_PARTY => PokeCrypto.SIZE_8PARTY;
    public override PK9 BlankPKM => new();
    public override Type PKMType => typeof(PK9);

    public override bool HasParty => true;
    public override int PartyCount { get => PartyCountValue; protected set => PartyCountValue = value; }
    public override int GetPartyOffset(int slot) => slot * SIZE_PARTY;
    public override int GetBoxOffset(int box) => box * 30 * SIZE_PARTY;

    protected override Span<byte> PartyBuffer => PartyDataBuffer;
    protected override Span<byte> BoxBuffer => BoxDataBuffer;
    protected override PK9 GetPKM(Memory<byte> data) => new(data);
    protected override void DecryptPKM(Span<byte> data) => PokeCrypto.Decrypt8(data);

    public override string GetString(ReadOnlySpan<byte> data) => StringConverter8.GetString(data);
    public override int LoadString(ReadOnlySpan<byte> data, Span<char> destBuffer) => StringConverter8.LoadString(data, destBuffer);
    public override int SetString(Span<byte> destBuffer, ReadOnlySpan<char> value, int maxLength, StringConverterOption option)
        => StringConverter8.SetString(destBuffer, value, maxLength, option);

    public override GameVersion Version { get => GameVersion.SL; set { } }
}
