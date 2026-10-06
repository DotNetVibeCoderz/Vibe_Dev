// Kas Warung - app.js
// Simple SPA logic to manage transactions persisted in localStorage.

const STORAGE_KEY = 'kas-warung_transactions';

// DOM elements (IDs required by tests)
const form = document.getElementById('transaction-form');
const inputDate = document.getElementById('date');
const inputDescription = document.getElementById('description');
const inputCategory = document.getElementById('category');
const inputAmount = document.getElementById('amount');
const inputType = document.getElementById('type');
const addBtn = document.getElementById('add-transaction');
const transactionsListEl = document.getElementById('transactions-list');
const balanceTodayEl = document.getElementById('balance-today');
const balanceMonthEl = document.getElementById('balance-month');

// In-memory transactions array
let transactions = [];

// Utility: format integer amount to Indonesian Rupiah string. e.g. 1234567 -> "Rp 1.234.567"
function formatRupiah(amount) {
  const abs = Math.abs(amount);
  return (amount < 0 ? '-' : '') + 'Rp ' + abs.toString().replace(/\B(?=(\d{3})+(?!\d))/g, '.');
}

// Utility: generate simple unique id
function generateId() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
}

// Load transactions from localStorage
function loadTransactions() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed;
  } catch (e) {
    console.error('Failed to load transactions', e);
    return [];
  }
}

// Save transactions to localStorage
function saveTransactions() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(transactions));
}

// Add a transaction object to state and persist
function addTransaction(tx) {
  transactions.unshift(tx); // newest first
  saveTransactions();
  render();
}

// Delete by id
function deleteTransaction(id) {
  transactions = transactions.filter(t => t.id !== id);
  saveTransactions();
  render();
}

// Compute balance for a given predicate (e.g., date or month)
function computeBalance(filterFn) {
  return transactions.reduce((acc, tx) => {
    if (!filterFn(tx)) return acc;
    return acc + (tx.type === 'in' ? tx.amount : -tx.amount);
  }, 0);
}

// Render transactions list and summaries
function render() {
  // Clear list
  transactionsListEl.innerHTML = '';

  // Render each transaction
  transactions.forEach(tx => {
    const item = document.createElement('div');
    item.className = 'transaction';

    const left = document.createElement('div');
    left.className = 'tx-left';

    const meta = document.createElement('div');
    meta.className = 'tx-meta';

    const dateEl = document.createElement('div');
    dateEl.className = 'tx-date';
    // display human readable date
    const d = new Date(tx.date + 'T00:00:00');
    dateEl.textContent = d.toLocaleDateString('id-ID', { year: 'numeric', month: 'short', day: 'numeric' });

    const descEl = document.createElement('div');
    descEl.className = 'tx-desc';
    descEl.textContent = tx.description;

    const catEl = document.createElement('div');
    catEl.className = 'tx-cat';
    catEl.textContent = tx.category;

    meta.appendChild(dateEl);
    meta.appendChild(descEl);
    meta.appendChild(catEl);

    left.appendChild(meta);

    const right = document.createElement('div');
    right.className = 'tx-right';

    const amountEl = document.createElement('div');
    amountEl.className = 'tx-amount';
    // display amount always positive; type badge shows masuk/keluar
    amountEl.textContent = formatRupiah(tx.amount);

    const typeBadge = document.createElement('div');
    typeBadge.className = tx.type === 'in' ? 'tx-type-in' : 'tx-type-out';
    typeBadge.textContent = tx.type === 'in' ? 'Masuk' : 'Keluar';

    const deleteBtn = document.createElement('button');
    deleteBtn.className = 'delete-btn';
    deleteBtn.setAttribute('data-id', tx.id);
    deleteBtn.textContent = 'Hapus';
    deleteBtn.addEventListener('click', () => {
      if (confirm('Hapus transaksi ini?')) {
        deleteTransaction(tx.id);
      }
    });

    right.appendChild(amountEl);
    right.appendChild(typeBadge);
    right.appendChild(deleteBtn);

    item.appendChild(left);
    item.appendChild(right);

    transactionsListEl.appendChild(item);
  });

  // Summaries
  const todayISO = new Date().toISOString().slice(0,10);
  const balanceToday = computeBalance(tx => tx.date === todayISO);
  const now = new Date();
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const balanceMonth = computeBalance(tx => tx.date.startsWith(`${year}-${month}-`));

  balanceTodayEl.textContent = formatRupiah(balanceToday);
  balanceMonthEl.textContent = formatRupiah(balanceMonth);
}

// Initialize: load and render
function init() {
  transactions = loadTransactions();
  // If date input supports valueAsDate, set default to today
  const today = new Date().toISOString().slice(0,10);
  if (inputDate) inputDate.value = today;
  render();

  // Form submit handling
  form.addEventListener('submit', (e) => {
    e.preventDefault();
    const date = inputDate.value;
    const description = inputDescription.value.trim();
    const category = inputCategory.value.trim();
    const amountVal = parseInt(inputAmount.value, 10);
    const type = inputType.value;

    if (!date || !description || !category || isNaN(amountVal)) {
      alert('Mohon isi semua field dengan benar');
      return;
    }

    const tx = {
      id: generateId(),
      date: date, // ISO date 'YYYY-MM-DD'
      description,
      category,
      amount: Math.abs(amountVal), // store positive integer
      type: type === 'in' ? 'in' : 'out'
    };

    addTransaction(tx);
    form.reset();
    inputDate.value = today; // reset date to today
  });
}

// Expose some functions for tests or debugging
window.KasWarung = {
  init,
  addTransaction,
  deleteTransaction,
  loadTransactions,
  saveTransactions,
  formatRupiah,
  get transactions() { return transactions; }
};

// Auto-init
init();
