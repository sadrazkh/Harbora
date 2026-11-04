// A public build variable: Vite writes its value into this bundle, so it is fixed when the site is built.
document.querySelector('#app').textContent = import.meta.env.VITE_GREETING;
