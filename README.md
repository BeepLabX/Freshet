# Freshet

<img src="src/Freshet/Assets/Freshet.ico" width="64" align="right" alt="">

winget으로 Windows 앱을 골라서 업데이트하고, 실제로 적용됐는지 확인해 이력으로 남기는 Windows x64 앱입니다.

## 실행

Windows 10/11 x64와 [winget](https://learn.microsoft.com/windows/package-manager/winget/)이 필요합니다. [Releases](../../releases)에서 ZIP을 받아 풀고 `Freshet.exe`를 실행합니다. .NET 런타임은 EXE에 포함되어 있습니다.

Freshet은 현재 사용자 권한으로 실행됩니다. 관리자 권한이 필요한 설치 프로그램은 패키지마다 Windows 사용자 계정 컨트롤(UAC) 확인 창을 표시할 수 있습니다. 배포 EXE에는 디지털 서명이 없어 처음 실행할 때 SmartScreen 경고가 나타날 수 있습니다. 배포 페이지의 SHA256으로 파일을 확인하세요.

## 기능

- 업데이트 목록 조회, 이름·패키지 ID 검색, 선택한 앱 순차 업데이트
- 설치 후 버전을 다시 조회해 완료·앱 재시작 필요·적용 확인 안 됨·실패를 구분
- 보류(목록에 남기고 선택 제외)와 숨김(재조회 후에도 제외), 숨김 목록에서 해제
- 설치 파일 예상 용량, 선택 합계, 로컬 앱 아이콘
- 현재 앱 완료 후 중지, 실행 로그, 업데이트 이력과 CSV 내보내기
- Windows 앱 테마(라이트·다크)와 고대비 모드 따라가기

용량은 설치 파일의 예상 크기이며 추가 다운로드·의존 패키지·설치 후 점유 공간과 다릅니다. 크기를 알 수 없는 앱은 0으로 합산하지 않습니다. 단위는 1 KB = 1,000바이트입니다.

## 데이터와 삭제

Freshet은 설치 등록, 서비스, 자동 실행 작업을 만들지 않습니다. 앱을 종료한 뒤 압축을 푼 폴더를 삭제하면 됩니다.

설정·보류·숨김·이력·로그·용량 캐시는 `%LOCALAPPDATA%\Freshet`에 저장됩니다. 완전히 지우려면 이 폴더도 삭제하세요. 조회 로그는 최근 30개만 보관합니다. Freshet을 삭제해도 Freshet으로 업데이트한 다른 앱은 그대로 남습니다. 자세한 내용은 [사용 안내](docs/usage.txt)를 참고하세요.

## 빌드와 검증

Windows와 .NET 10 SDK가 필요합니다. SDK 버전은 [global.json](global.json)을 따릅니다.

```powershell
.\scripts\build.ps1
.\scripts\verify.ps1 -UseBuildHost
.\scripts\package.ps1
```

빌드 결과는 `artifacts/publish/win-x64/Freshet.exe`, 배포 ZIP과 SHA256은 `artifacts/releases/<버전>/`에 생성됩니다.

검증은 모의 설치와 WPF 화면 렌더링(라이트·다크·최소 크기)을 사용하며 실제 패키지를 업데이트하지 않습니다. `-LiveScan`은 실제 winget 읽기 전용 조회를, `-LiveMetadata`는 아이콘·설치 파일 헤더 조회를 추가합니다. 결과와 화면은 `artifacts/verification/`에 저장됩니다. GUI 검증에는 Windows 데스크톱 세션이 필요합니다.

앱 아이콘은 `scripts/make-icon.ps1`이 코드로 그려 생성합니다.

| 경로 | 내용 |
| --- | --- |
| `src/Freshet/` | C# · WPF · .NET 10 앱 소스 |
| `scripts/` | 빌드·검증·패키징·아이콘 생성 |
| `docs/` | 사용자 문서 |

## 사용권

소스 코드는 공개되어 있지만 오픈소스 라이선스는 아닙니다. 수정·재배포·상업적 이용에 대한 별도 허락을 부여하지 않습니다. 자세한 내용은 [LICENSE](LICENSE)를 확인하세요. 풀 리퀘스트는 받지 않으며 버그 제보와 제안은 Issues로 받습니다.

Pretendard 글꼴(SIL Open Font License 1.1)과 .NET 런타임(MIT) 고지는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에 있습니다.

Freshet은 Microsoft 공식 제품이 아니며 Microsoft와 관련이 없습니다. Windows와 winget은 Microsoft의 상표입니다.
