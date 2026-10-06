import os
from html.parser import HTMLParser

BASE = os.path.join(os.path.dirname(__file__), '..', 'apps', 'kas-warung')
INDEX = os.path.join(BASE, 'index.html')
APPJS = os.path.join(BASE, 'app.js')

REQUIRED_IDS = [
    'transaction-form',
    'date',
    'description',
    'category',
    'amount',
    'type',
    'add-transaction',
    'transactions-list',
    'balance-today',
    'balance-month',
]


class IdCollectingParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids = {}  # id -> (tag, attrs)
        self.options_for_select = {}  # select_id -> list of option values
        self._current_select_id = None
        self.radio_inputs = []  # list of (name, value, id, attrs)

    def handle_starttag(self, tag, attrs):
        ad = dict(attrs)
        if 'id' in ad:
            self.ids[ad['id']] = (tag, ad)
        # capture options when inside a select with id
        if tag == 'select':
            self._current_select_id = ad.get('id')
            if self._current_select_id:
                self.options_for_select.setdefault(self._current_select_id, [])
        if tag == 'option' and self._current_select_id is not None:
            # option may have value attribute
            val = ad.get('value')
            self.options_for_select.setdefault(self._current_select_id, []).append(val)
        # radio inputs
        if tag == 'input':
            t = ad.get('type', '').lower()
            name = ad.get('name')
            val = ad.get('value')
            if t == 'radio' and name is not None:
                self.radio_inputs.append((name, val, ad.get('id'), ad))

    def handle_endtag(self, tag):
        if tag == 'select':
            self._current_select_id = None


def test_index_html_exists():
    assert os.path.isfile(INDEX), f"Missing index.html at {INDEX}"


def test_html_contains_required_ids_and_controls():
    with open(INDEX, 'r', encoding='utf-8') as f:
        html = f.read()

    parser = IdCollectingParser()
    parser.feed(html)

    # Check required ids present
    missing = [i for i in REQUIRED_IDS if i not in parser.ids]
    assert not missing, f"Missing required ids in index.html: {missing}"

    # transaction-form must be a form
    tag, attrs = parser.ids['transaction-form']
    assert tag == 'form', f"Element with id 'transaction-form' should be a <form>, found <{tag}>"

    # date, description, category, amount should be input elements (or at least present)
    for inp in ['date', 'description', 'category', 'amount']:
        tag, attrs = parser.ids[inp]
        assert tag in ('input', 'textarea', 'select'), f"Expected '{inp}' to be an input-like element, found <{tag}>"

    # type control: either a select with options or radio inputs
    type_tag, type_attrs = parser.ids['type']
    if type_tag == 'select':
        options = parser.options_for_select.get('type', [])
        assert 'in' in options and 'out' in options, f"Select#type must have option values 'in' and 'out'; found {options}"
    else:
        # maybe radios named 'type'
        radios = [r for r in parser.radio_inputs if r[0] == 'type']
        values = [r[1] for r in radios]
        assert 'in' in values and 'out' in values, f"Type control not a select and no radio inputs with values 'in' and 'out' found; select tag is <{type_tag}>"

    # submit button
    btn_tag, btn_attrs = parser.ids['add-transaction']
    assert btn_tag in ('button', 'input'), f"Submit control with id 'add-transaction' should be <button> or <input>, found <{btn_tag}>"

    # transactions-list and balance ids exist (already checked) but ensure transactions-list is a container
    tl_tag, _ = parser.ids['transactions-list']
    assert tl_tag in ('div', 'section', 'ul'), f"transactions-list should be a container element, found <{tl_tag}>"


def test_app_js_references_same_ids():
    assert os.path.isfile(APPJS), f"Missing app.js at {APPJS}"
    with open(APPJS, 'r', encoding='utf-8') as f:
        js = f.read()

    missing_refs = [i for i in REQUIRED_IDS if i not in js]
    assert not missing_refs, f"app.js does not reference these ids found in HTML: {missing_refs}"


def test_smoke_js_add_transaction_presence():
    # Since we cannot execute JS here, check that the JS exposes or defines addTransaction-like behavior
    with open(APPJS, 'r', encoding='utf-8') as f:
        js = f.read()

    found = False
    hints = []
    if 'function addTransaction' in js or 'addTransaction = function' in js:
        found = True
        hints.append('function addTransaction')
    if 'window.KasWarung' in js and 'addTransaction' in js:
        found = True
        hints.append('window.KasWarung.addTransaction')
    if 'addTransaction(' in js and 'function' not in js:
        # might be referenced; include as hint
        hints.append('addTransaction(')
    assert found, f"app.js does not appear to define or expose addTransaction behavior. Hints found: {hints}"
