// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Submit confirmation for any <form class="js-confirm" data-confirm="...">.
//
// Deliberately NOT an inline onsubmit="confirm('@value')": the confirmation text carries
// GitHub cost-center names and operator-supplied principal labels. Razor HTML-encodes those
// into the attribute, but a browser decodes an attribute value BEFORE handing it to the
// JavaScript parser, so an apostrophe would close the string literal and anything after it
// would execute. Read as text via dataset, a quote is just a quote.
document.addEventListener('submit', function (e) {
    var form = e.target.closest('form.js-confirm');
    if (form && !window.confirm(form.dataset.confirm || 'Are you sure?')) {
        e.preventDefault();
    }
});
