// ===== NAVBAR SCROLL =====
const navbar = document.getElementById('navbar');
window.addEventListener('scroll', () => {
  navbar.classList.toggle('scrolled', window.scrollY > 60);
});

// ===== MOBILE NAV TOGGLE =====
const navToggle = document.getElementById('navToggle');
const navLinks = document.querySelector('.nav-links');
navToggle?.addEventListener('click', () => {
  navLinks.classList.toggle('open');
});
document.querySelectorAll('.nav-links a').forEach(link => {
  link.addEventListener('click', () => navLinks.classList.remove('open'));
});

// ===== CONTACT FORM =====
const form = document.getElementById('contactForm');
const formSuccess = document.getElementById('formSuccess');

form?.addEventListener('submit', (e) => {
  e.preventDefault();
  const name = form.querySelector('#name').value.trim();
  const phone = form.querySelector('#phone').value.trim();
  if (!name || !phone) {
    alert('Kérjük töltse ki a kötelező mezőket (Név, Telefon)!');
    return;
  }
  // Simulate send (replace with real backend/email service)
  const btn = form.querySelector('.btn-submit');
  btn.disabled = true;
  btn.querySelector('span').textContent = 'Küldés...';
  setTimeout(() => {
    form.style.display = 'none';
    formSuccess.classList.add('visible');
  }, 800);
});

// ===== SCROLL REVEAL =====
const observerOptions = { threshold: 0.12, rootMargin: '0px 0px -40px 0px' };
const observer = new IntersectionObserver((entries) => {
  entries.forEach(entry => {
    if (entry.isIntersecting) {
      entry.target.classList.add('revealed');
      observer.unobserve(entry.target);
    }
  });
}, observerOptions);

document.querySelectorAll(
  '.highlight-card, .feature-category, .gallery-item, .location-item, .floor-card, .contact-item'
).forEach((el, i) => {
  el.style.opacity = '0';
  el.style.transform = 'translateY(24px)';
  el.style.transition = `opacity 0.5s ease ${i * 0.07}s, transform 0.5s ease ${i * 0.07}s`;
  observer.observe(el);
});

document.addEventListener('animationend', () => {}, { once: true });

// Apply reveal class via CSS
const style = document.createElement('style');
style.textContent = '.revealed { opacity: 1 !important; transform: translateY(0) !important; }';
document.head.appendChild(style);

// ===== SMOOTH STAT COUNTER ANIMATION =====
function animateValue(el, start, end, duration, suffix = '') {
  const startTime = performance.now();
  const update = (currentTime) => {
    const elapsed = currentTime - startTime;
    const progress = Math.min(elapsed / duration, 1);
    const eased = 1 - Math.pow(1 - progress, 3);
    const current = Math.floor(start + (end - start) * eased);
    el.textContent = current + suffix;
    if (progress < 1) requestAnimationFrame(update);
  };
  requestAnimationFrame(update);
}

const heroObserver = new IntersectionObserver((entries) => {
  entries.forEach(entry => {
    if (entry.isIntersecting) {
      const statValues = document.querySelectorAll('.stat-value');
      const data = [
        { value: 120, suffix: ' m²' },
        { value: 4, suffix: '' },
        { value: 2, suffix: '' },
        { value: 200, suffix: ' m²' },
      ];
      statValues.forEach((el, i) => {
        if (data[i]) animateValue(el, 0, data[i].value, 1200, data[i].suffix);
      });
      heroObserver.disconnect();
    }
  });
}, { threshold: 0.5 });

const heroStats = document.querySelector('.hero-stats');
if (heroStats) heroObserver.observe(heroStats);
