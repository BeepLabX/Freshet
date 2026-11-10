# winget 준비

Freshet은 .NET에서 winget.exe를 직접 실행합니다.

| 상황 | 대응 |
| --- | --- |
| 실행 별칭을 찾지 못함 | 등록된 Microsoft App Installer 패키지 경로도 확인 |
| winget 미설치 | 앱의 ‘앱 설치 관리자 열기’로 설치 또는 복구 후 새로고침 |
| Store 사용 불가 | Microsoft의 수동 설치 안내 또는 조직에서 허용한 배포 경로 확인 |
| 조직 정책 제한 | 관리자에게 허용 여부 확인 |
| 원본 약관 필요 | 설정의 ‘원본 약관 동의 후 다시 조회’ 사용 |
| 원본·네트워크 오류 | 실행 로그 확인 후 재조회 |

공식 안내: [winget](https://learn.microsoft.com/windows/package-manager/winget/), [앱 설치 관리자](https://learn.microsoft.com/windows/msix/app-installer/install-update-app-installer).
