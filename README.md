# 교무톡 - 서버 관리자

학교가 자체 서버를 운영하는 교무톡의 서버 관리자 제품 저장소입니다. MIT 라이선스입니다.

## 사용과 운영

교직원 PC·교사용 앱은 교내 업무 서버, 학교톡(학생·학부모용 앱)은 학교 소식 서버에 접속합니다. 승인된 학교 검색 목록에서 학교를 선택합니다. 이 저장소는 학교 계정·메시지·학생 명부를 보관하는 장소가 아닙니다.

도입·운영 내부결재 승인 문서는 각 학교에 보관하세요. 필요한 학운위 심의·보안 검토·개인정보 처리 절차를 따로 확인해야 합니다. 학교 검색 등록 승인은 그 절차나 보안 인증을 대신하지 않습니다. 결재 원본·운영 설정·학교 검토 자료는 공개 GitHub에 올리지 마세요.

- [공통 사용 설명서](docs/public/00_먼저읽기.md)
- [관리자 설치·운영](docs/public/01_관리자_설치와운영.md)
- [교직원 사용 안내](docs/public/02_교직원_사용안내.md)
- [학교 내부기안 작성양식](docs/public/학교도입_내부기안_작성양식.txt)
- [분리 저장소와 호환성 관리](docs/repository-split.md)

## 빌드

.NET SDK는 global.json에 고정합니다. Windows 제품은 64비트 Windows가 필요하며, Mac은 macOS·Xcode 26.6이 필요합니다.

```powershell
./scripts/Build.ps1 -SkipTests
./scripts/Build-ServerInstaller.ps1
```

GitHub Actions의 제품별 검증이 성공하면 Artifacts에서 시험 산출물을 내려받습니다. 현재 모두 1.0.0 시험본이며 정식 Release는 아직 발행하지 않았습니다. 정식 Release를 발행하면 PC 프로그램이 서명된 업데이트 피드를 하루 한 번 확인합니다([업데이트 안내](docs/desktop-updates.md)). Windows 일반 배포용 코드 서명과 Mac Developer ID 서명·공증은 별도 준비가 필요합니다.

서버: [gyomutalk-server](https://github.com/yd043105-pixel/gyomutalk-server) / Windows: [gyomutalk-windows](https://github.com/yd043105-pixel/gyomutalk-windows) / Mac: [gyomutalk-mac](https://github.com/yd043105-pixel/gyomutalk-mac)

모바일 앱과 기존 이력은 [기존 저장소](https://github.com/yd043105-pixel/yymessenger)에 유지합니다. 앱용 새 저장소는 만들지 않았습니다. 설치·앱·데이터 식별자는 호환성을 위해 유지합니다.
