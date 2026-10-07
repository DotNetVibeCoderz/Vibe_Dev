from playwright.sync_api import sync_playwright
import os
with sync_playwright() as p:
    browser=p.chromium.launch(headless=True, executable_path=r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe')
    page=browser.new_page(viewport={'width':1280,'height':800})
    page.goto('http://localhost:5080', wait_until='networkidle'); page.screenshot(path='screenshots/warungweb-home.png')
    page.get_by_role('button', name='Tambah').nth(0).click(); page.get_by_role('button', name='Tambah').nth(1).click(); page.wait_for_timeout(500); page.screenshot(path='screenshots/warungweb-cart.png')
    mobile=browser.new_page(viewport={'width':390,'height':844}); mobile.goto('http://localhost:5080', wait_until='networkidle'); mobile.screenshot(path='screenshots/warungweb-mobile.png')
    docker=browser.new_page(viewport={'width':1280,'height':800}); docker.goto('http://localhost:8090', wait_until='networkidle'); docker.screenshot(path='screenshots/warungweb-docker.png')
    browser.close()
