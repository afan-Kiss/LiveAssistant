换机安装包目录（Deploy/MachineSetup）
=====================================

打包旧电脑时，请把下列文件放进本目录对应子文件夹，拷到新电脑点歌系统旁，
然后打开管理后台 → 系统设置 → 换机部署 → 一键安装。

1) VBCable/
   - VBCABLE_Setup_x64.exe（https://vb-audio.com/Cable/）

2) sidecars/
   - 抖音直播弹幕助手.exe
   - 酷狗api_v1.5.exe
   - kgapijs/ 整个文件夹（内含 app.js）

3) Kuaishou/（可选）
   - ks-ui-server.jar
   - jre/ 便携 Java（可选）

4) AiSpeech/（AI 语音）
   - Ollama/OllamaSetup.exe  或  Ollama/ 便携目录（含 ollama.exe）
   - GPT-SoVITS/ 完整工程（必须含 service/main.py）
   说明：模型用后台「只拉 AI 模型」按 Config 里 aiSpeech.model 下载（需联网）；
         也可直接拷贝 %USERPROFILE%\.ollama\models 到新机同路径；
         conda 环境 GPTSoVits 需在新机自行安装。

另外请单独备份：
   - 点歌系统 Config/ 与 data/
   - 抖音/酷狗登录态、快手 Cookie

VB-CABLE / Ollama 安装需要管理员权限；CABLE 装完建议重启。
