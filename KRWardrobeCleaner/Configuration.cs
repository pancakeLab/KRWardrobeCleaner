using Dalamud.Configuration;

namespace KRWardrobeCleaner;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 8;

    public bool Stage1IncludeDyed { get; set; }
    public bool Stage1IncludePlateRegistered { get; set; }

    public bool Stage2IncludeDyed { get; set; }
    /// 마테리아가 장착된 장비도 환상의 옷장 저장을 시도한다(게임이 거절할 수 있음). 기본 꺼짐.
    public bool Stage2IncludeMateria { get; set; }
    /// 장비함 조각을 가방으로 옮기기 위해 옷장을 닫았다가, 근처 옷장 오브젝트와 상호작용해 자동으로 다시 연다.
    public bool Stage2AutoReopenDresser { get; set; } = true;
    /// 단벌 맡기기 방식. 0 = 기록된 콜백 (14, 행, 0) [권장], 1 = 에이전트 선택 인덱스 + 맡기기 대기 플래그, 2 = 콜백 값 직접 지정.
    public int Stage2SingleMethod { get; set; }
    /// 방식 2에서 보낼 콜백 값. 쉼표 구분, u 접두어 = uint, {row} = 목록 행 번호. 예: u14,{row},u0
    public string Stage2SingleCallback { get; set; } = "u14,{row},u0";

    public bool Stage4SmartIncludeCrafterGatherer { get; set; }
    public int Stage4SmartMaxLevel { get; set; } = 100;

    /// 옷장 중복 정리: 같은 아이템이 여러 개면 염색된 것을 남기고 나머지를 중복분으로 본다.
    public bool DuplicateKeepDyedFirst { get; set; } = true;

    // ---- 3. 장비함 ↔ 가방 ----
    /// 0 = 장비함 → 가방, 1 = 가방 → 장비함 (GearTransfer.Direction)
    public int TransferDirection { get; set; }
    /// 장비함 → 가방일 때 대상 카테고리. -1 = 장비함 전체, 그 외 GearTransfer.Categories 인덱스.
    public int TransferSourceCategory { get; set; } = -1;
    /// 장비함 → 가방일 때 4번 가방 빈칸에만 넣는다.
    public bool TransferToBag4Only { get; set; }
    /// 장비함 → 가방일 때 장비세트에 등록된 장비는 제외한다.
    public bool TransferExcludeGearset { get; set; } = true;

    /// 필터 1: 직업. 선택한 직업(기본 클래스 포함) 중 하나라도 착용 가능한 장비.
    public bool FilterJobEnabled { get; set; }
    public List<string> FilterJobs { get; set; } = [];

    /// 필터 2: 착용 레벨 범위. Op: 0 = 그리고(AND), 1 = 또는(OR) — 앞 필터와의 결합.
    public bool FilterLevelEnabled { get; set; }
    public int FilterLevelMin { get; set; } = 1;
    public int FilterLevelMax { get; set; } = 100;
    public int FilterLevelOp { get; set; }

    /// 필터 3: 환상의 옷장 보유 여부. Owned: true = 보유중, false = 미보유.
    public bool FilterDresserEnabled { get; set; }
    public bool FilterDresserOwned { get; set; }
    public int FilterDresserOp { get; set; }

    /// 켜면 후보 판정·세트 저장 요청·검증 결과를 Dalamud 로그(/xllog)에 "[KWC]" 접두어로 남긴다.
    public bool DebugLog { get; set; }
}
