"""Create the fictional Gulf Lantern dataset, never customer or provider records."""
from datetime import datetime, timedelta, timezone
from pathlib import Path
import json, uuid

ROOT=Path(__file__).resolve().parents[1]
def identity(key):return str(uuid.uuid5(uuid.NAMESPACE_URL,'https://gulf-lantern.example.invalid/'+key))
menu_specs=[
 ('smoked-fish-dip','Smoked fish dip','Smoked Gulf-style fish, house crackers, pickled jalapeno and lemon.',1100,'share','smoked-fish-dip'),
 ('beer-cheese-pretzel','Warm pretzel & beer cheese','A soft salted pretzel with warm beer cheese and grain mustard.',1050,'share','beer-cheese-pretzel'),
 ('coconut-shrimp','Coconut shrimp','Crisp coconut shrimp with orange-chile dipping sauce.',1600,'share','coconut-shrimp'),
 ('calamari','Crispy calamari','Lightly fried rings, lemon, parsley and house marinara.',1400,'share','calamari'),
 ('street-corn','Charred street corn','Lime crema, cotija, cilantro and smoked paprika.',800,'share','street-corn'),
 ('loaded-fries','Lantern loaded fries','Golden fries, beer cheese, scallions and smoky house sauce.',1000,'share','loaded-fries'),
 ('gulf-tacos','Gulf fish tacos','Blackened fish, citrus slaw, lime crema and warm tortillas.',1700,'kitchen','gulf-tacos'),
 ('sunset-burger','Sunset burger','Smash-griddled beef, cheddar, lettuce, tomato and fries.',1800,'kitchen','sunset-burger'),
 ('citrus-wings','Citrus-glazed wings','Twelve wings tossed in orange-lime glaze with a little heat.',1750,'kitchen','citrus-wings'),
 ('margherita-flatbread','Margherita flatbread','Tomato, mozzarella, fresh basil and a crisp flatbread crust.',1500,'kitchen','margherita-flatbread'),
 ('daily-catch','Grilled Gulf-style catch','Seasonal grilled fish, vegetables and lemon-herb butter.',2600,'kitchen','daily-catch'),
 ('smash-sliders','Beachside smash sliders','Two cheddar sliders with pickles and a side of fries.',1600,'kitchen','smash-sliders'),
 ('shrimp-basket','Coconut shrimp basket','A larger serving of our coconut shrimp with fries and citrus slaw.',2100,'kitchen','coconut-shrimp'),
 ('wing-platter','Wings for the table','Twenty-four citrus-glazed wings, celery and two dips to share.',3200,'share','citrus-wings'),
 ('sunset-spritz','Sunset spritz','Aperitif, sparkling wine, orange and soda over ice.',1200,'bar','sunset-spritz'),
 ('spicy-margarita','Spicy beach margarita','Tequila, fresh lime, agave and jalapeno with a salted rim.',1300,'bar','spicy-margarita'),
 ('espresso-martini','Lantern espresso martini','Vodka, coffee liqueur and freshly pulled espresso.',1400,'bar','espresso-martini'),
 ('local-draft','Rotating local-style draft','A crisp rotating draft pour. Ask your bartender for the selection.',700,'bar','local-draft'),
 ('key-lime-fizz','Key lime fizz','Alcohol-free key lime, sparkling soda and a fresh lime wheel.',650,'soft','key-lime-fizz'),
 ('iced-tea','Fresh-brewed iced tea','Black tea over ice with lemon; sweet or unsweet.',400,'soft','iced-tea')]
people=[('owner','Casey Brooks','Owner','owner','Business care, menu, billing and daily operations'),
 ('manager','Jordan Ellis','General manager','manager','Run the shift, schedules, menu and events'),
 ('bartender','Nico Rivera','Bartender','bartender','Order board, drinks, training and team updates'),
 ('server-maya','Maya Chen','Server','server','Tables, unpaid checks, service and team training'),
 ('server-eli','Eli Parker','Server','server','Patio service, order handoff and customer care'),
 ('kitchen','Luis Santos','Kitchen lead','kitchen','Accept, prepare and ready the kitchen orders'),
 ('driver','Zoe Reed','Delivery staff','driver','Assigned deliveries and delivery training'),
 ('avery','Avery Lane','Customer','customer','A regular, collecting rewards and bringing friends'),
 ('morgan','Morgan Blake','Customer','customer','A patio dinner and a sunset music reservation'),
 ('taylor','Taylor Quinn','Customer','customer','Pickup lunch with a food allergy note'),
 ('quinn','Quinn Foster','Customer','customer','A delivery order and a weekend event'),
 ('reese','Reese Walker','Customer','customer','A first visit, alcohol-free drinks and rewards')]
now=datetime.now(timezone.utc)
friday=(now+timedelta(days=(4-now.weekday())%7 or 7)).replace(hour=23,minute=0,second=0,microsecond=0)
event_specs=[('Sunset Strings','A fictional acoustic duo on the patio. Easy coastal covers, space for conversation and a Gulf sunset.',friday,54),
 ('Lantern Afterglow','A fictional soul-and-groove trio for a relaxed Saturday evening. Patio seating and full dinner menu.',friday+timedelta(days=1),40),
 ('Sunday Saltwater Sessions','A fictional afternoon jazz set with guitar and upright bass. Alcohol-free drinks welcome.',friday+timedelta(days=2,hours=-7),32)]
data={'id':'gulf-lantern','slug':'gulf-lantern','name':'Gulf Lantern Beach Bar',
 'location':'St. Pete Beach, Florida','address':'Sunset patio, St. Pete Beach, FL 33706 - fictional venue',
 'tagline':'Pull up a chair. Stay for the sunset.','description':'An easygoing neighborhood beach bar: Gulf-style plates, a friendly patio crew and live music as the lanterns come on.',
 'hours':'Mon-Thu 11 am-10 pm; Fri-Sat 11 am-midnight; Sun 11 am-9 pm',
 'phone':'(727) 555-0147','email':'hello@gulf-lantern.example.invalid',
 'hero':'/sample-bar/gulf-lantern-hero.png','fictional':True,'password':'GulfLantern-demo-only!',
 'taxNote':'Illustrative 7% test tax, not a verified location-specific tax setting.',
 'categories':[{'id':'share','label':'For the table'},{'id':'kitchen','label':'Beach kitchen'},{'id':'bar','label':'From the bar'},{'id':'soft','label':'Zero-proof & refreshing'}],
 'menu':[dict(id=k,name=n,description=d,priceCents=p,category=c,image='/demo/assets/menu/'+im+'.webp') for k,n,d,p,c,im in menu_specs],
 'people':[dict(key=k,id=identity(k),name=n,title=t,role=r,bio=b,email=k+'@gulf-lantern.example.invalid') for k,n,t,r,b in people],
 'events':[dict(id=identity(title),title=title,details=details,location='Gulf Lantern sunset patio (fictional)',startsAt=start.isoformat(),endsAt=(start+timedelta(hours=2)).isoformat(),timeZone='America/New_York',capacity=capacity) for title,details,start,capacity in event_specs]}
target=ROOT/'fixtures/gulf-lantern.json';target.parent.mkdir(exist_ok=True)
if target.exists():raise SystemExit('Dataset already exists; preserve its chosen event dates and identities.')
target.write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'menuItems':len(data['menu']),'staffPlusManager':6,'customers':5,'events':3,'fixture':str(target)}))
