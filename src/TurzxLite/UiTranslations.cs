namespace TurzxLite;

public static partial class Localization
{
    // Keys are the original Portuguese display text. Persisted modes, metric IDs,
    // file extensions, user-entered labels and media paths are never rewritten.
    private static partial void AddUiTranslations(Dictionary<string, string> translations)
    {
        // Main window, tray and preview.
        translations["Sobrepor métricas à imagem / vídeo"] = "Overlay metrics on the image / video";
        translations["Conectar automaticamente ao abrir"] = "Connect automatically on launch";
        translations["Abrir minimizado na bandeja"] = "Start minimized in the system tray";
        translations["Iniciar com o Windows"] = "Start with Windows";
        translations["Pronto para conectar"] = "Ready to connect";
        translations["Brilho · 70%"] = "Brightness · 70%";
        translations["Brilho · {0}%"] = "Brightness · {0}%";
        translations["Brilho · "] = "Brightness · ";
        translations["Conectar / aplicar"] = "Connect / apply";
        translations["Testar prévia"] = "Preview";
        translations["Parar"] = "Stop";
        translations["Ativar temperatura da CPU"] = "Enable CPU temperature";
        translations["Reiniciar sensor da CPU"] = "Restart CPU sensor";
        translations["Editar layout / escolher informações"] = "Edit layout / choose metrics";
        translations["Sem HWiNFO ou AIDA64 · acesso autorizado pelo Windows"] = "No HWiNFO or AIDA64 · Windows-authorized access";
        translations["Sua tela. Menos trabalho para o PC."] = "Your display. Less work for your PC.";
        translations["PRÉVIA  ·  480 × 480"] = "PREVIEW  ·  480 × 480";
        translations["TELA"] = "DISPLAY";
        translations["Buscar"] = "Scan";
        translations["CONTEÚDO"] = "CONTENT";
        translations["Rotação"] = "Rotation";
        translations["FFMPEG  ·  DECODIFICADOR DE VÍDEO"] = "FFMPEG  ·  VIDEO DECODER";
        translations["Selecione ffmpeg.exe"] = "Select ffmpeg.exe";
        translations["Feche o TURZX original antes de conectar."] = "Close the original TURZX software before connecting.";
        translations["Arquivo selecionado. Clique em "] = "File selected. Click ";
        translations["Arquivo selecionado. Clique em Reproduzir vídeo."] = "File selected. Click Play video.";
        translations["Arquivo selecionado. Clique em Conectar / aplicar."] = "File selected. Click Connect / apply.";
        translations["Reproduzir vídeo"] = "Play video";
        translations["Abrir TURZX Lite"] = "Open TURZX Lite";
        translations["Parar envio"] = "Stop sending";
        translations["Sair"] = "Exit";
        translations["{0} tela(s) encontrada(s) · pronta para conectar"] = "{0} display(s) found · ready to connect";
        translations[" tela(s) encontrada(s) · pronta para conectar"] = " display(s) found · ready to connect";
        translations["Tela não encontrada. Verifique o cabo USB e clique em Buscar."] = "Display not found. Check the USB cable and click Scan.";
        translations["Nenhuma tela selecionada. Clique em Buscar."] = "No display selected. Click Scan.";
        translations["CPU °C indisponível · use Ativar temperatura da CPU. Passe o mouse para detalhes."] = "CPU °C unavailable · click Enable CPU temperature. Hover for details.";
        translations["Temperatura da CPU indisponível"] = "CPU temperature unavailable";
        translations["CPU {0:0}°C · {1} · integrado, 1 Hz"] = "CPU {0:0}°C · {1} · built-in, 1 Hz";
        translations[" · integrado, 1 Hz"] = " · built-in, 1 Hz";
        translations["Aplicativo: CPU {0:0.00}% · RAM {1:0} MB"] = "App: CPU {0:0.00}% · RAM {1:0} MB";
        translations["Aplicativo: CPU "] = "App: CPU ";
        translations["Prévia leve: 2 fps · o display usa os FPS selecionados.\nO vídeo é decodificado pela própria tela."] = "Lightweight preview: 2 fps · the display uses the selected FPS.\nVideo is decoded by the display itself.";
        translations["USB direto · atualização controlada\nA prévia funciona mesmo sem a tela conectada."] = "Direct USB · controlled refresh\nPreview works without a connected display.";
        translations["Preparando o editor de informações…"] = "Preparing the layout editor…";
        translations["Layout salvo. As informações serão atualizadas na tela."] = "Layout saved. The display will update with your changes.";
        translations["Fundo do editor indisponível: "] = "Editor background unavailable: ";
        translations["Editor: "] = "Editor: ";
        translations["TURZX LITE\n\nClique em Testar prévia"] = "TURZX LITE\n\nClick Preview";

        // Content selectors and file dialogs. These names translate button text only.
        translations["Painel"] = "Dashboard";
        translations["Imagem"] = "Image";
        translations["Vídeo"] = "Video";
        translations["Escolher arquivo…"] = "Choose file…";
        translations["Modo {0}"] = "{0} mode";
        translations["Modo Painel"] = "Dashboard mode";
        translations["Modo Imagem"] = "Image mode";
        translations["Modo Vídeo"] = "Video mode";
        translations["Arquivo de mídia"] = "Media file";
        translations["Selecione Painel, Imagem ou Vídeo."] = "Select Dashboard, Image or Video.";
        translations["Escolha o vídeo para a tela TURZX"] = "Choose a video for the TURZX display";
        translations["Escolha a imagem para a tela TURZX"] = "Choose an image for the TURZX display";
        translations["Vídeos"] = "Videos";
        translations["Imagens"] = "Images";
        translations["Todos os arquivos"] = "All files";
        translations["Vídeos|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.gif;*.h264|Todos os arquivos|*.*"] = "Videos|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.gif;*.h264|All files|*.*";
        translations["Imagens|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Todos os arquivos|*.*"] = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*";
        translations["Modo selecionado"] = "Selected mode";
        translations["Clique para selecionar"] = "Click to select";
        translations["Escolha um vídeo para reproduzir em repetição"] = "Choose a video to play on repeat";
        translations["Escolha sua imagem"] = "Choose your image";
        translations["Escolher vídeo…"] = "Choose video…";
        translations["Escolher imagem…"] = "Choose image…";
        translations["Escolha um vídeo e clique em Conectar / aplicar. Reprodução em repetição."] = "Choose a video and click Connect / apply. Playback repeats automatically.";
        translations["Escolha uma imagem e clique em Conectar / aplicar."] = "Choose an image and click Connect / apply.";
        translations["CPU, memória e relógio. Para animações, selecione Vídeo."] = "CPU, memory and clock. For animations, select Video.";

        // Layout editor controls, instructions and validation.
        translations["Negrito"] = "Bold";
        translations["Mostrar rótulo"] = "Show label";
        translations["Mostrar guias no editor"] = "Show editor guides";
        translations["Selecione um item"] = "Select an item";
        translations["ITENS · marque para exibir"] = "ITEMS · check to show";
        translations["ITENS · {0}/40 · marque para exibir"] = "ITEMS · {0}/40 · check to show";
        translations["ITENS · "] = "ITEMS · ";
        translations["/40 · marque para exibir"] = "/40 · check to show";
        translations["Editor da tela redonda · TURZX Lite"] = "Round display editor · TURZX Lite";
        translations["MONTE SUA TELA"] = "DESIGN YOUR DISPLAY";
        translations["Adicione informações, arraste os textos e ajuste tamanho e cor. A tela física só muda depois de salvar e aplicar."] = "Add metrics, drag text, and adjust its size and color. The physical display updates after you save and apply.";
        translations["ADICIONAR INFORMAÇÃO"] = "ADD METRIC";
        translations["+ Adicionar"] = "+ Add";
        translations["Texto: "] = "Text: ";
        translations["Duplicar"] = "Duplicate";
        translations["Remover"] = "Remove";
        translations["↑ Frente"] = "↑ Forward";
        translations["↓ Fundo"] = "↓ Backward";
        translations["Arraste para mover · Setas: 1 px · Shift: 10 px\nDelete: remover · Prévia 480 × 480\nSensor indisponível: —"] = "Drag to move · Arrows: 1 px · Shift: 10 px\nDelete: remove · 480 × 480 preview\nUnavailable sensor: —";
        translations["POSIÇÃO DO CENTRO · pixels"] = "CENTER POSITION · pixels";
        translations["TAMANHO DO TEXTO"] = "TEXT SIZE";
        translations["RÓTULO"] = "LABEL";
        translations["COR DO TEXTO"] = "TEXT COLOR";
        translations["Cor…"] = "Color…";
        translations["FUNDO E PRÉVIA"] = "BACKGROUND AND PREVIEW";
        translations["Cor de fundo…"] = "Background color…";
        translations["Dados da sessão atual; ausentes: —.\nArrastar aqui não envia comandos USB."] = "Current session data; unavailable: —.\nDragging here does not send USB commands.";
        translations["Restaurar padrão"] = "Restore defaults";
        translations["Compacto"] = "Compact";
        translations["Limpar"] = "Clear";
        translations["Cancelar"] = "Cancel";
        translations["Salvar layout"] = "Save layout";
        translations["Este layout já tem 40 itens. Remova um item antes de adicionar outro."] = "This layout already has 40 items. Remove an item before adding another.";
        translations["Limite do layout"] = "Layout limit";

        // Metric names and categories. IDs such as cpu.temperature stay unchanged.
        translations["Processador"] = "Processor";
        translations["Placa de vídeo"] = "Graphics card";
        translations["Memória"] = "Memory";
        translations["Sistema"] = "System";
        translations["Personalizado"] = "Custom";
        translations["Temperatura da CPU"] = "CPU temperature";
        translations["Uso da CPU"] = "CPU usage";
        translations["Frequência média da CPU"] = "Average CPU clock";
        translations["Potência da CPU"] = "CPU power";
        translations["Modelo do processador"] = "Processor model";
        translations["Processadores lógicos"] = "Logical processors";
        translations["Temperatura da GPU"] = "GPU temperature";
        translations["Uso da GPU"] = "GPU usage";
        translations["VRAM utilizada"] = "VRAM used";
        translations["VRAM total"] = "Total VRAM";
        translations["Uso de VRAM em %"] = "VRAM usage in %";
        translations["Frequência da GPU"] = "GPU clock";
        translations["Frequência da VRAM"] = "VRAM clock";
        translations["Potência da GPU"] = "GPU power";
        translations["Ventoinha da GPU"] = "GPU fan";
        translations["Fan GPU"] = "GPU fan";
        translations["Modelo da placa de vídeo"] = "Graphics card model";
        translations["Uso da RAM em %"] = "RAM usage in %";
        translations["RAM utilizada"] = "RAM used";
        translations["RAM disponível"] = "Available RAM";
        translations["RAM livre"] = "Free RAM";
        translations["RAM total"] = "Total RAM";
        translations["RAM utilizada / total"] = "RAM used / total";
        translations["Relógio"] = "Clock";
        translations["Data"] = "Date";
        translations["Tempo ligado"] = "Uptime";
        translations["Ligado"] = "Uptime";
        translations["Texto livre"] = "Custom text";
        translations["Seu texto"] = "Your text";

        // Startup and diagnostic messages which can surface in dialogs.
        translations["O TURZX Lite já está aberto. Procure o ícone ao lado do relógio."] = "TURZX Lite is already running. Look for its icon in the system tray.";
        translations["Erro fatal"] = "Fatal error";
        translations["CPU auxiliar: "] = "CPU helper: ";
        translations["Display TURZX não encontrado."] = "TURZX display not found.";
        translations["Display não encontrado."] = "Display not found.";
        translations["Tela não encontrada."] = "Display not found.";
        translations["Selecione --video arquivo."] = "Specify --video followed by a file path.";
    }
}
