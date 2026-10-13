using Dalamud.Configuration;

namespace KRWardrobeCleaner;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 7;

    public bool Stage1IncludeDyed { get; set; }
    public bool Stage1IncludePlateRegistered { get; set; }

    public bool Stage2IncludeDyed { get; set; }
    /// 마테리아가 장착된 장비도 환상의 옷장 저장을 시도한다(게임이 거절할 수 있음). 기본 꺼짐.
    public bool Stage2IncludeMateria { get; set; }

    public bool Stage3SmartIncludeCrafterGatherer { get; set; }
    public int Stage3SmartMaxLevel { get; set; } = 100;

    public bool Stage4SmartIncludeCrafterGatherer { get; set; }
    public int Stage4SmartMaxLevel { get; set; } = 100;

    /// 옷장 중복 정리: 같은 아이템이 여러 개면 염색된 것을 남기고 나머지를 중복분으로 본다.
    public bool DuplicateKeepDyedFirst { get; set; } = true;

    /// 장비함 → 4번 가방: 마지막으로 고른 카테고리(ArmoryCategoryMover.Categories 인덱스).
    public int ArmoryMoveCategoryIndex { get; set; } = 2;

    /// 켜면 후보 판정·세트 저장 요청·검증 결과를 Dalamud 로그(/xllog)에 "[KWC]" 접두어로 남긴다.
    public bool DebugLog { get; set; }
}
