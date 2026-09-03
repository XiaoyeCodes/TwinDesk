# -*- coding: utf-8 -*-
"""
浏览器内操作Windows应用 - 可行性验证Demo后端 (v3: 全局输入模拟)
功能: 捕获指定窗口画面 -> WebSocket推流给浏览器 -> 接收浏览器鼠标键盘事件,
      用SendInput/mouse_event等全局输入模拟方式操作目标窗口

注意: 这个方式会移动你电脑真实的系统鼠标, 这是本机demo阶段的正常现象
(因为浏览器和目标窗口在同一台电脑上); 到了服务器部署场景不存在这个问题。

运行环境: Windows + Python 3.9+
安装依赖:
    pip install websockets mss pillow pywin32

运行:
    python server.py
然后用浏览器打开 index.html 即可看到画面并操作。
"""

import asyncio
import json
import io

import mss
import win32gui
import win32con
import win32api
import win32process
from PIL import Image
import websockets

# ==================== 配置区 ====================
TARGET_WINDOW_TITLE = "Notepad++"

FPS = 15
JPEG_QUALITY = 60

VK_MAP = {
    "Enter": 0x0D, "Backspace": 0x08, "Tab": 0x09, "Escape": 0x1B,
    "Shift": 0x10, "Control": 0x11, "Alt": 0x12,
    "ArrowUp": 0x26, "ArrowDown": 0x28, "ArrowLeft": 0x25, "ArrowRight": 0x27,
    "Delete": 0x2E, " ": 0x20,
}


def get_vk(js_key: str):
    if js_key in VK_MAP:
        return VK_MAP[js_key]
    if len(js_key) == 1:
        return win32api.VkKeyScan(js_key) & 0xFF
    return None


def find_window_handle(title_substr: str):
    result = []

    def enum_handler(hwnd, _):
        if win32gui.IsWindowVisible(hwnd) and title_substr in win32gui.GetWindowText(hwnd):
            result.append(hwnd)

    win32gui.EnumWindows(enum_handler, None)
    return result[0] if result else None


def get_window_rect(hwnd):
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    return {"left": left, "top": top, "width": right - left, "height": bottom - top}


def debug_print_all_windows():
    print("当前系统中可见的窗口标题列表:")

    def enum_handler(hwnd, _):
        title = win32gui.GetWindowText(hwnd)
        if win32gui.IsWindowVisible(hwnd) and title.strip():
            print(f"  - 「{title}」")

    win32gui.EnumWindows(enum_handler, None)
    print("-" * 40)


def bring_to_foreground(hwnd):
    """把目标窗口切到前台/激活状态, 键盘输入才会正确路由过去"""
    try:
        cur_thread = win32api.GetCurrentThreadId()
        target_thread, _ = win32process.GetWindowThreadProcessId(hwnd)
        win32process.AttachThreadInput(cur_thread, target_thread, True)
        result = win32gui.SetForegroundWindow(hwnd)
        win32process.AttachThreadInput(cur_thread, target_thread, False)
        current_fg = win32gui.GetForegroundWindow()
        success = (current_fg == hwnd)
        print(f"[调试] 切前台窗口: SetForegroundWindow返回={result}, 切换后前台窗口是否为目标={success}")
        return success
    except Exception as e:
        print(f"[调试] 切换前台窗口出错: {e}")
        return False


def is_admin():
    try:
        import ctypes
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return None


async def stream_frames(websocket):
    with mss.mss() as sct:
        while True:
            hwnd = find_window_handle(TARGET_WINDOW_TITLE)
            if hwnd is None:
                print(f"未找到标题包含「{TARGET_WINDOW_TITLE}」的窗口, 0.5秒后重试...")
                await asyncio.sleep(0.5)
                continue

            bbox = get_window_rect(hwnd)
            if bbox["width"] <= 0 or bbox["height"] <= 0:
                await asyncio.sleep(0.2)
                continue

            try:
                raw = sct.grab(bbox)
            except Exception:
                await asyncio.sleep(0.2)
                continue

            pil_img = Image.frombytes("RGB", raw.size, raw.bgra, "raw", "BGRX")
            buf = io.BytesIO()
            pil_img.save(buf, format="JPEG", quality=JPEG_QUALITY)
            data = buf.getvalue()

            header = bbox["width"].to_bytes(2, "big") + bbox["height"].to_bytes(2, "big")

            try:
                await websocket.send(header + data)
            except websockets.ConnectionClosed:
                break

            await asyncio.sleep(1 / FPS)


async def handle_input(websocket):
    """用全局输入模拟(真实移动系统鼠标+真实按键)操作目标窗口
    兼容性最好, 但会动到你本机真实的鼠标指针(本机demo阶段的正常现象)"""
    async for message in websocket:
        try:
            msg = json.loads(message)
        except Exception:
            continue

        hwnd = find_window_handle(TARGET_WINDOW_TITLE)
        if hwnd is None:
            continue

        bbox = get_window_rect(hwnd)
        mtype = msg.get("type")

        if mtype == "mousemove":
            x, y = bbox["left"] + msg["x"], bbox["top"] + msg["y"]
            win32api.SetCursorPos((x, y))
        elif mtype == "mousedown":
            x, y = bbox["left"] + msg["x"], bbox["top"] + msg["y"]
            print(f"[调试] 点击目标屏幕坐标: ({x}, {y}), 窗口区域: {bbox}")
            win32api.SetCursorPos((x, y))
            bring_to_foreground(hwnd)
            win32api.mouse_event(win32con.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
        elif mtype == "mouseup":
            win32api.mouse_event(win32con.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
        elif mtype == "keydown":
            vk = get_vk(msg.get("key", ""))
            if vk:
                win32api.keybd_event(vk, 0, 0, 0)
        elif mtype == "keyup":
            vk = get_vk(msg.get("key", ""))
            if vk:
                win32api.keybd_event(vk, 0, win32con.KEYEVENTF_KEYUP, 0)


async def handler(websocket):
    consumer_task = asyncio.ensure_future(handle_input(websocket))
    producer_task = asyncio.ensure_future(stream_frames(websocket))
    done, pending = await asyncio.wait(
        [consumer_task, producer_task], return_when=asyncio.FIRST_COMPLETED
    )
    for task in pending:
        task.cancel()


async def main():
    debug_print_all_windows()
    admin_status = is_admin()
    print(f"[调试] 当前Python进程是否以管理员身份运行: {admin_status}")
    print(f"WebSocket服务已启动: ws://localhost:8766")
    print(f"正在监视窗口标题包含: 「{TARGET_WINDOW_TITLE}」的窗口")
    print("请确保该窗口已经打开且完全可见(不要被浏览器等其他窗口遮挡), 然后用浏览器打开 index.html")
    async with websockets.serve(handler, "localhost", 8766, max_size=None):
        await asyncio.Future()


if __name__ == "__main__":
    asyncio.run(main())
