document.querySelectorAll('.api-price-card').forEach(card => {
  const choice=card.querySelector('input[type=checkbox]'); if(!choice)return;
  const total=card.querySelector('.api-total dd'), checkout=card.querySelector('a.api-button'), list=card.querySelector('.api-totals');
  const update=()=>{
    total.textContent=choice.checked?'$649':'$349';
    checkout.href='https://tide.casa/purchase/business'+(choice.checked?'?appStores=true':'');
    let row=list.querySelector('[data-store-row]');
    if(choice.checked&&!row){row=document.createElement('div');row.dataset.storeRow='';row.innerHTML='<dt>Optional store package</dt><dd>$300</dd>';list.insertBefore(row,list.children[1]);}
    if(!choice.checked&&row)row.remove();
  };
  choice.addEventListener('change',update);update();
});
