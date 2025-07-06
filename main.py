import json
from PyQt5.QtCore import QObject, pyqtSignal, pyqtSlot
from PyQt5.QtWebEngineWidgets import QWebEngineView, QWebEngineScript
from PyQt5.QtWidgets import QApplication, QMainWindow, QStatusBar

class Bridge(QObject):
    """
    Python与JavaScript桥梁类，专门用于接收网页新消息
    """
    webMessageReceived = pyqtSignal(str, str)  # 信号：标题, 内容

    @pyqtSlot(str, str)
    def handleWebMessage(self, title, content):
        """
        处理来自网页的消息
        :param title: 消息标题
        :param content: 消息内容
        """
        # 添加额外过滤条件（可选）
        if title and content:  # 确保不是空消息
            self.webMessageReceived.emit(title, content)

class HTMLViewer(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("网页消息监控器")
        self.resize(800, 600)
        
        # 初始化UI
        self.init_ui()
        
        # 初始化桥接
        self.init_bridge()
        
        # 设置状态栏
        self.status_bar = QStatusBar()
        self.setStatusBar(self.status_bar)
    
    def init_ui(self):
        """初始化用户界面"""
        self.web_view = QWebEngineView()
        self.setCentralWidget(self.web_view)
        
        # 页面加载完成信号
        self.web_view.page().loadFinished.connect(self.on_load_finished)
    
    def init_bridge(self):
        """初始化Python-JavaScript桥接"""
        self.bridge = Bridge()
        self.bridge.webMessageReceived.connect(self.handle_web_message)
        
        # 将桥接对象暴露给JavaScript
        self.web_view.page().setWebChannel(self.bridge)
        self.web_view.page().runJavaScript("""
            // 创建webChannel并连接桥接对象
            new QWebChannel(qt.webChannelTransport, function(channel) {
                window.pyBridge = channel.objects.bridge;
            });
        """)
    
    def load_url(self, url):
        """加载指定URL"""
        self.web_view.setUrl(url)
        self.show_status_message(f"正在加载: {url}")
    
    def inject_notification_handler(self):
        """注入JavaScript代码以捕获网页新消息"""
        script_source = """
        // 1. 定义消息处理器，只处理标记为新消息的通知
        window.handleWebMessage = function(title, content) {
            if (window.pyBridge && title && content) {
                window.pyBridge.handleWebMessage(title, content);
            }
        };
        
        // 2. 覆盖原生通知系统
        if (window.Notification) {
            const OriginalNotification = window.Notification;
            
            window.Notification = function(title, options) {
                // 只转发标记为网页消息的通知
                if (options && options.isWebMessage === true) {
                    window.handleWebMessage(title, options.body || "");
                }
                return new OriginalNotification(title, options);
            };
            
            // 保持其他Notification功能不变
            window.Notification.permission = OriginalNotification.permission;
            window.Notification.requestPermission = OriginalNotification.requestPermission.bind(OriginalNotification);
        }
        
        // 3. 监听网页消息事件
        window.addEventListener('message', function(event) {
            // 只处理特定格式的消息
            if (event.data && event.data.type === 'web_new_message') {
                window.handleWebMessage(
                    event.data.title || '网页消息',
                    event.data.content || JSON.stringify(event.data)
                );
            }
        });
        
        // 4. 确保常见回调函数存在
        if (typeof allMessageCallback === 'undefined') {
            window.allMessageCallback = function(data) {
                if (data && data.isNewMessage === true) {
                    window.handleWebMessage(
                        data.title || '回调消息',
                        data.content || JSON.stringify(data)
                    );
                }
            };
        }
        """
        
        script = QWebEngineScript()
        script.setSourceCode(script_source)
        script.setInjectionPoint(QWebEngineScript.DocumentCreation)
        script.setWorldId(QWebEngineScript.MainWorld)
        script.setRunsOnSubFrames(True)
        self.web_view.page().scripts().insert(script)
    
    def on_load_finished(self, success):
        """页面加载完成回调"""
        if success:
            self.show_status_message("页面加载完成")
            self.inject_notification_handler()
            
            # 注入配置信息（可选）
            self.web_view.page().runJavaScript("""
                window.webMessageConfig = {
                    version: '1.0',
                    minPriority: 1  // 只接收优先级≥1的消息
                };
            """)
        else:
            self.show_status_message("页面加载失败")
    
    def handle_web_message(self, title, content):
        """
        处理来自网页的新消息
        :param title: 消息标题
        :param content: 消息内容
        """
        self.show_status_message(f"新消息: {title}")
        self.show_notification(title, content)
        print(f"[网页消息] {title}: {content}")
    
    def show_notification(self, title, message):
        """显示系统通知（可根据需要实现）"""
        # 这里可以替换为实际的通知系统实现
        print(f"显示通知 - {title}: {message}")
    
    def show_status_message(self, message):
        """在状态栏显示消息"""
        self.status_bar.showMessage(message)
        print(f"[状态] {message}")

if __name__ == "__main__":
    app = QApplication([])
    
    viewer = HTMLViewer()
    viewer.show()
    
    # 加载测试页面或实际目标网页
    viewer.load_url("https://example.com")  # 替换为实际网址
    
    app.exec_()
